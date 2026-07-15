using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AuroraSuite
{
    public class SyncResult
    {
        public int TitlesScannedOnConsole { get; set; }
        public int TitlesMatchedInLibrary { get; set; }
        public int FilesUploaded { get; set; }
        public int FilesSkippedNoRemoteMatch { get; set; }
        public int Errors { get; set; }
    }

    public class SyncEngine
    {
        private readonly Settings _settings;
        private readonly Action<string> _log;

        public SyncEngine(Settings settings, Action<string> log)
        {
            _settings = settings;
            _log = log;
        }

        public static string ExtractTitleId(string folderName)
        {
            var idx = folderName.IndexOf('_');
            var titleId = idx >= 0 ? folderName.Substring(0, idx) : folderName;
            return titleId.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Scans the local library folder and builds one row per Title ID subfolder,
        /// detecting which of the four asset files (GL/GC/BK/SS) actually exist for
        /// it. This is what populates the Sync tab's grid - it does NOT touch the
        /// console at all, purely a local filesystem scan.
        /// </summary>
        public static List<LibraryRow> LoadLibraryRows(string libraryPath)
        {
            var rows = new List<LibraryRow>();
            if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
                return rows;

            foreach (var dir in Directory.GetDirectories(libraryPath).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var titleId = Path.GetFileName(dir)!.Trim().ToUpperInvariant();
                bool hasIcon = File.Exists(Path.Combine(dir, $"GL{titleId}.asset"));
                bool hasBoxart = File.Exists(Path.Combine(dir, $"GC{titleId}.asset"));
                bool hasBackground = File.Exists(Path.Combine(dir, $"BK{titleId}.asset"));
                bool hasScreenshots = File.Exists(Path.Combine(dir, $"SS{titleId}.asset"));

                rows.Add(new LibraryRow(titleId, dir, hasIcon, hasBoxart, hasBackground, hasScreenshots));
            }

            return rows;
        }

        /// <summary>
        /// Uploads only the rows that are both ticked (Include) and have at least one
        /// checked asset type present locally, over one FTP connection. Updates each
        /// row's Status/Detail directly as it goes - safe to call from a background
        /// thread; WPF marshals PropertyChanged delivery for already-bound items on
        /// its own (same pattern XenonArchivist's SafeCopier relies on), so no
        /// Dispatcher wrapping is needed here.
        /// </summary>
        public SyncResult RunSelected(IList<LibraryRow> rows, CancellationToken token)
        {
            var result = new SyncResult();
            var included = rows.Where(r => r.Include).ToList();

            if (included.Count == 0)
            {
                _log("Nothing selected - tick at least one row (or use Select All) before syncing.");
                return result;
            }

            // FTP or XBDM - the engine below doesn't care which, it just talks to a
            // transport. XBDM needs no username/password at all.
            using var transport = TransportFactory.Create(_settings, _log);
            transport.Connect();

            var basePath = transport.ResolveBasePath(_settings.AuroraGameDataPath);
            _log($"Listing {basePath} ...");
            var folderNames = transport.ListDirectoryNames(basePath);
            result.TitlesScannedOnConsole = folderNames.Count;
            _log($"Found {folderNames.Count} title folder(s) on the console.");

            // Extracted Title ID -> first console folder name that matches it.
            var consoleByTitleId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folderNames)
            {
                var tid = ExtractTitleId(f);
                if (!consoleByTitleId.ContainsKey(tid))
                    consoleByTitleId[tid] = f;
            }

            foreach (var row in included)
            {
                token.ThrowIfCancellationRequested();

                row.Status = SyncStatus.Syncing;
                row.Detail = "";

                if (!consoleByTitleId.TryGetValue(row.TitleId, out var folderName))
                {
                    row.Status = SyncStatus.NotMatchedOnConsole;
                    row.Detail = "No matching game folder found on the console.";
                    _log($"[{row.TitleId}] no matching console folder - skipped");
                    continue;
                }

                result.TitlesMatchedInLibrary++;
                var remoteTitlePath = transport.CombinePath(basePath, folderName);
                _log($"[{row.TitleId}] matched console folder '{folderName}'");

                HashSet<string> remoteFileNames;
                try
                {
                    remoteFileNames = transport.ListFileNames(remoteTitlePath);
                }
                catch (Exception ex)
                {
                    row.Status = SyncStatus.Failed;
                    row.Detail = $"Could not list remote folder: {ex.Message}";
                    _log($"[{row.TitleId}]  ! Could not list '{remoteTitlePath}': {ex.Message}");
                    result.Errors++;
                    continue;
                }

                int uploaded = 0, skipped = 0, errors = 0;
                var details = new List<string>();

                foreach (var prefix in _settings.AssetPrefixes)
                {
                    var localFileName = $"{prefix}{row.TitleId}.asset";
                    var localFilePath = Path.Combine(row.FolderPath, localFileName);
                    if (!File.Exists(localFilePath))
                        continue;

                    var existsRemotely = remoteFileNames.Contains(localFileName);
                    if (!existsRemotely && _settings.OnlyOverwriteExisting)
                    {
                        skipped++;
                        details.Add($"{prefix} skipped (not on console)");
                        _log($"[{row.TitleId}]  - {localFileName}: no matching file on console, skipping (overwrite-only mode)");
                        result.FilesSkippedNoRemoteMatch++;
                        continue;
                    }

                    try
                    {
                        transport.UploadFile(localFilePath, remoteTitlePath, localFileName);

                        var localSize = new FileInfo(localFilePath).Length;
                        var remoteSize = transport.GetFileSize(remoteTitlePath, localFileName);

                        if (remoteSize == null)
                        {
                            uploaded++;
                            details.Add($"{prefix} uploaded (unverified)");
                            _log($"[{row.TitleId}]  + Uploaded {localFileName} (could not read the size back to verify)");
                            result.FilesUploaded++;
                        }
                        else if (remoteSize.Value != localSize)
                        {
                            errors++;
                            details.Add($"{prefix} size mismatch after upload");
                            _log($"[{row.TitleId}]  ! {localFileName}: uploaded but remote size ({remoteSize.Value}) doesn't match local ({localSize})");
                            result.Errors++;
                        }
                        else
                        {
                            uploaded++;
                            details.Add($"{prefix} uploaded ({remoteSize.Value} bytes)");
                            _log($"[{row.TitleId}]  + Uploaded {localFileName} (verified {remoteSize.Value} bytes)");
                            result.FilesUploaded++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        details.Add($"{prefix} failed: {ex.Message}");
                        _log($"[{row.TitleId}]  ! Failed to upload {localFileName}: {ex.Message}");
                        result.Errors++;
                    }
                }

                row.Status = errors > 0 ? SyncStatus.Failed : (skipped > 0 ? SyncStatus.SyncedWithSkips : SyncStatus.Synced);
                row.Detail = details.Count > 0
                    ? string.Join("; ", details)
                    : "Nothing to upload (no checked asset type had a matching local file)";
            }

            transport.Quit();
            _log("Done.");
            return result;
        }
    }
}
