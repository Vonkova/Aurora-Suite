using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AuroraSuite
{
    public class ImageAssetOptions
    {
        public bool ConvertIcon { get; set; } = true;
        public bool ConvertBoxart { get; set; } = true;
        public bool ConvertBackground { get; set; } = true;
        public bool ConvertScreenshots { get; set; } = true;
    }

    public class ImageAssetSummary
    {
        public int TitlesProcessed;
        public int AssetsWritten;
    }

    /// <summary>
    /// C# port of the Python aurora_asset_tool.py / aurora_screenshot_tool.py pipeline
    /// (folder detection -> cover-fit resize -> ARGB -> AuroraAsset.dll via bridge.exe
    /// -> container packing), extended to:
    ///   - auto-detect a single title folder vs. a parent-of-many-titles folder
    ///     (equivalent to the old --title/--folder vs --batch flags)
    ///   - auto-detect the "already made all 4 assets" structured layout
    ///     (TitleID/Coverart, /Background, /Icon, /Screenshots) alongside the
    ///     original flat "images directly in the title folder" layout
    ///   - combine every image found in a Screenshots folder into one .asset with
    ///     multiple screenshot slots, instead of only using the first one
    ///
    /// Split into Scan() (detection only, builds the grid's rows) and
    /// ConvertSelected() (does the actual conversion for ticked rows), mirroring
    /// SyncEngine's LoadLibraryRows()/RunSelected() split on the Sync tab.
    /// </summary>
    public static class ImageAssetConverter
    {
        private static readonly string[] IconNames = { "icon", "logo", "gl" };
        private static readonly string[] BoxartNames = { "boxart", "cover", "gc", "box" };
        private static readonly string[] BackgroundNames = { "background", "bg", "backdrop", "bk" };
        private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp" };
        private static readonly Regex ScreenshotNameRegex =
            new(@"^(screenshot|screen|ss)[\s_\-]*\d*$", RegexOptions.IgnoreCase);

        private class ImagePlan
        {
            public string? Icon;
            public string? Boxart;
            public string? Background;
            public List<string> Screenshots = new();
            public bool IsEmpty => Icon == null && Boxart == null && Background == null && Screenshots.Count == 0;
        }

        /// <summary>
        /// Detection only - builds one row per detected title folder (single-folder or
        /// batch, auto-detected) with no conversion performed. Populates the Image
        /// Assets tab's grid.
        /// </summary>
        public static List<ImageAssetRow> Scan(string sourcePath, Action<string> log, CancellationToken token)
        {
            var rows = new List<ImageAssetRow>();

            if (!Directory.Exists(sourcePath))
                throw new Exception($"Source folder does not exist: {sourcePath}");

            var sourceDir = new DirectoryInfo(sourcePath);

            if (LooksLikeTitleFolder(sourceDir))
            {
                log($"Single-folder mode: treating '{sourceDir.Name}' itself as one title.");
                AddScannedRow(sourceDir, sourceDir.Name, log, rows);
            }
            else
            {
                var subdirs = sourceDir.GetDirectories().OrderBy(d => d.Name).ToArray();
                log($"Batch mode: {subdirs.Length} subfolder(s) found under '{sourceDir.FullName}'.");
                foreach (var sub in subdirs)
                {
                    token.ThrowIfCancellationRequested();
                    AddScannedRow(sub, sub.Name, log, rows);
                }
            }

            return rows;
        }

        private static void AddScannedRow(DirectoryInfo dir, string titleIdRaw, Action<string> log, List<ImageAssetRow> rows)
        {
            var titleId = titleIdRaw.Trim().ToUpperInvariant();
            bool structured = HasStructuredSubfolders(dir);
            var plan = DetectPlan(dir, out var unclaimed);

            if (structured)
                log($"[{titleId}] found named subfolder(s) (Coverart/Background/Icon/Screenshots)");

            if (unclaimed.Count > 0)
            {
                log($"[{titleId}] note: {unclaimed.Count} extra image(s) in the root weren't used " +
                    "(Boxart already has one, either from a subfolder/name match or the first extra image found): " +
                    string.Join(", ", unclaimed.Select(Path.GetFileName)));
            }

            var row = new ImageAssetRow(titleId, dir.FullName, plan.Icon, plan.Boxart, plan.Background, plan.Screenshots);

            if (plan.IsEmpty)
            {
                row.Status = ConvertStatus.NothingDetected;
                row.Include = false;
                log($"[{titleId}] no recognizable images found");
            }

            rows.Add(row);
        }

        /// <summary>
        /// Converts only the ticked rows. Updates each row's Status/Detail directly as
        /// it goes - safe to call from a background thread, same reasoning as
        /// SyncEngine.RunSelected (WPF marshals PropertyChanged for already-bound items
        /// on its own).
        /// </summary>
        public static ImageAssetSummary ConvertSelected(IList<ImageAssetRow> rows, string outputPath,
            ImageAssetOptions options, Action<string> log, CancellationToken token)
        {
            var summary = new ImageAssetSummary();
            var included = rows.Where(r => r.Include).ToList();

            if (included.Count == 0)
            {
                log("Nothing selected - tick at least one row (or use Select All) before converting.");
                return summary;
            }

            Directory.CreateDirectory(outputPath);

            foreach (var row in included)
            {
                token.ThrowIfCancellationRequested();

                row.Status = ConvertStatus.Converting;
                row.Detail = "";

                var titleOutDir = Path.Combine(outputPath, row.TitleId);
                var details = new List<string>();
                int errors = 0;
                bool wroteAny = false;

                if (options.ConvertIcon && row.IconPath != null &&
                    ConvertSingle(row.IconPath, row.TitleId, "Icon", AssetContainerBuilder.TypeIcon, "GL", 64, 64, false, titleOutDir, log, details, ref errors))
                {
                    wroteAny = true;
                    summary.AssetsWritten++;
                }

                if (options.ConvertBoxart && row.BoxartPath != null &&
                    ConvertSingle(row.BoxartPath, row.TitleId, "Boxart", AssetContainerBuilder.TypeBoxart, "GC", 900, 600, true, titleOutDir, log, details, ref errors))
                {
                    wroteAny = true;
                    summary.AssetsWritten++;
                }

                if (options.ConvertBackground && row.BackgroundPath != null &&
                    ConvertSingle(row.BackgroundPath, row.TitleId, "Background", AssetContainerBuilder.TypeBackground, "BK", 1280, 720, true, titleOutDir, log, details, ref errors))
                {
                    wroteAny = true;
                    summary.AssetsWritten++;
                }

                if (options.ConvertScreenshots && row.ScreenshotPaths.Count > 0 &&
                    ConvertScreenshots(row.ScreenshotPaths, row.TitleId, titleOutDir, log, details, ref errors))
                {
                    wroteAny = true;
                    summary.AssetsWritten++;
                }

                row.Detail = details.Count > 0 ? string.Join("; ", details) : "No checked asset type had a usable image for this title.";

                row.Status = (wroteAny, errors > 0) switch
                {
                    (true, false) => ConvertStatus.Converted,
                    (true, true) => ConvertStatus.ConvertedWithSkips,
                    (false, true) => ConvertStatus.Failed,
                    (false, false) => ConvertStatus.NothingDetected,
                };

                if (wroteAny)
                    summary.TitlesProcessed++;
            }

            log("Done.");
            return summary;
        }

        private static bool LooksLikeTitleFolder(DirectoryInfo dir)
        {
            if (HasStructuredSubfolders(dir)) return true;
            return dir.GetFiles().Any(f => ImageExts.Contains(f.Extension.ToLowerInvariant()));
        }

        private static bool HasStructuredSubfolders(DirectoryInfo dir)
        {
            return FindSubfolder(dir, "coverart") != null
                || FindSubfolder(dir, "boxart") != null
                || FindSubfolder(dir, "background") != null
                || FindSubfolder(dir, "icon") != null
                || FindSubfolder(dir, "screenshots") != null;
        }

        private static DirectoryInfo? FindSubfolder(DirectoryInfo dir, string name)
        {
            return dir.GetDirectories()
                .FirstOrDefault(d => string.Equals(d.Name.Replace(" ", ""), name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ConvertSingle(string imagePath, string titleId, string specName, int typeIndex,
            string prefix, int w, int h, bool compress, string titleOutDir, Action<string> log,
            List<string> details, ref int errors)
        {
            try
            {
                using var img = ImageProcessing.LoadValidImage(imagePath);
                using var resized = ImageProcessing.CoverResize(img, w, h);
                var raw = ImageProcessing.ImageToRawArgb(resized);
                var (header, video) = AssetBridge.Encode(raw, w, h, compress);

                var entries = new SortedDictionary<int, (byte[], byte[])> { [typeIndex] = (header, video) };
                var container = AssetContainerBuilder.Build(entries);

                Directory.CreateDirectory(titleOutDir);
                var outPath = Path.Combine(titleOutDir, $"{prefix}{titleId}.asset");
                File.WriteAllBytes(outPath, container);

                log($"[{titleId}] {specName,-10} ({Path.GetFileName(imagePath)}) -> {Path.GetFileName(outPath)}  ({container.Length} bytes)");
                details.Add($"{specName} converted");
                return true;
            }
            catch (Exception ex)
            {
                log($"[{titleId}] FAILED {specName} ({Path.GetFileName(imagePath)}): {ex.Message}");
                details.Add($"{specName} failed: {ex.Message}");
                errors++;
                return false;
            }
        }

        /// <summary>
        /// Packs every screenshot image found into its own slot (5..24, 20 max) inside
        /// ONE SS&lt;title&gt;.asset - this is the piece the original Python screenshot
        /// tool didn't do (it only ever used the first image, slot 5).
        /// </summary>
        private static bool ConvertScreenshots(List<string> images, string titleId, string titleOutDir,
            Action<string> log, List<string> details, ref int errors)
        {
            var entries = new SortedDictionary<int, (byte[], byte[])>();
            int slot = AssetContainerBuilder.TypeScreenshotStart;
            // Valid asset-table slots only go up to AssetMax-1 (24) - the table has
            // exactly AssetMax (25) rows, indices 0..24.
            int maxSlot = AssetContainerBuilder.AssetMax - 1;
            int usedCount = 0;

            foreach (var imagePath in images)
            {
                if (slot > maxSlot)
                {
                    log($"[{titleId}] more than 20 screenshots found - extra image(s) ignored");
                    break;
                }

                try
                {
                    using var img = ImageProcessing.LoadValidImage(imagePath);
                    using var resized = ImageProcessing.CoverResize(img, 1280, 720);
                    var raw = ImageProcessing.ImageToRawArgb(resized);
                    var (header, video) = AssetBridge.Encode(raw, 1280, 720, true);

                    entries[slot] = (header, video);
                    log($"[{titleId}] screenshot slot {slot - AssetContainerBuilder.TypeScreenshotStart + 1} ({Path.GetFileName(imagePath)}) encoded");
                    slot++;
                    usedCount++;
                }
                catch (Exception ex)
                {
                    log($"[{titleId}] BAD SCREENSHOT {Path.GetFileName(imagePath)}: {ex.Message} -> skipping");
                    details.Add($"screenshot {Path.GetFileName(imagePath)} failed: {ex.Message}");
                    errors++;
                }
            }

            if (entries.Count == 0)
            {
                log($"[{titleId}] no usable screenshots");
                return false;
            }

            var container = AssetContainerBuilder.Build(entries);
            Directory.CreateDirectory(titleOutDir);
            var outPath = Path.Combine(titleOutDir, $"SS{titleId}.asset");
            File.WriteAllBytes(outPath, container);

            log($"[{titleId}] screenshots ({usedCount} image(s)) -> {Path.GetFileName(outPath)}  ({container.Length} bytes)");
            details.Add($"Screenshots converted ({usedCount} image(s))");
            return true;
        }

        // ---- Detection: subfolders and root images are checked TOGETHER, per asset
        // type, so they can never conflict with each other - a Screenshots subfolder
        // existing doesn't stop a root icon.png from also being picked up, etc.
        //
        // Per type, in priority order:
        //   1) the matching named subfolder (Icon/, Coverart or Boxart/, Background/,
        //      Screenshots/), if present - first image inside it (first several, for
        //      Screenshots)
        //   2) a recognizably-named image sitting directly in the title folder
        //      (icon.png/logo.png, boxart.png/cover.png, background.png/bg.png,
        //      screenshot*.png/ss*.png)
        //   3) if Boxart is still unset after 1) and 2), the first (alphabetically)
        //      root image left over unclaimed by any other type becomes Boxart/
        //      Coverart - any additional leftover images are reported (not silently
        //      dropped) but don't block the pick.
        private static ImagePlan DetectPlan(DirectoryInfo dir, out List<string> unclaimedRootImages)
        {
            var plan = new ImagePlan();

            var iconDir = FindSubfolder(dir, "icon");
            var boxDir = FindSubfolder(dir, "coverart") ?? FindSubfolder(dir, "boxart");
            var bgDir = FindSubfolder(dir, "background");
            var ssDir = FindSubfolder(dir, "screenshots");

            var rootImages = dir.GetFiles()
                .Where(f => ImageExts.Contains(f.Extension.ToLowerInvariant()))
                .OrderBy(f => f.Name)
                .ToList();

            var claimedFromRoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? MatchOneOf(string[] names) => rootImages
                .FirstOrDefault(f => names.Contains(Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant()))
                ?.FullName;

            plan.Icon = iconDir != null ? FirstImage(iconDir) : MatchOneOf(IconNames);
            if (iconDir == null && plan.Icon != null) claimedFromRoot.Add(plan.Icon);

            plan.Boxart = boxDir != null ? FirstImage(boxDir) : MatchOneOf(BoxartNames);
            if (boxDir == null && plan.Boxart != null) claimedFromRoot.Add(plan.Boxart);

            plan.Background = bgDir != null ? FirstImage(bgDir) : MatchOneOf(BackgroundNames);
            if (bgDir == null && plan.Background != null) claimedFromRoot.Add(plan.Background);

            if (ssDir != null)
            {
                plan.Screenshots = AllImages(ssDir);
            }
            else
            {
                plan.Screenshots = rootImages
                    .Where(f => !claimedFromRoot.Contains(f.FullName) &&
                                ScreenshotNameRegex.IsMatch(Path.GetFileNameWithoutExtension(f.Name)))
                    .OrderBy(f => f.Name)
                    .Select(f => f.FullName)
                    .ToList();
                foreach (var s in plan.Screenshots) claimedFromRoot.Add(s);
            }

            var leftover = rootImages
                .Where(f => !claimedFromRoot.Contains(f.FullName))
                .Select(f => f.FullName)
                .ToList();

            // If Boxart is still unset, pick the FIRST leftover root image (already
            // alphabetically sorted) as Boxart/Coverart, even when several are left
            // over - refusing outright whenever more than one existed made "nothing
            // detected" the common case for any real-world dump where a title folder
            // has more than one loose, unnamed image (multiple cover variants, stray
            // extra pictures, etc.). A deterministic pick plus a clear log note about
            // what got ignored is far more useful than converting nothing at all.
            if (plan.Boxart == null && leftover.Count > 0)
            {
                plan.Boxart = leftover[0];
                unclaimedRootImages = leftover.Skip(1).ToList();
            }
            else
            {
                unclaimedRootImages = leftover;
            }

            return plan;
        }

        private static string? FirstImage(DirectoryInfo dir) => dir.GetFiles()
            .Where(f => ImageExts.Contains(f.Extension.ToLowerInvariant()))
            .OrderBy(f => f.Name)
            .Select(f => f.FullName)
            .FirstOrDefault();

        private static List<string> AllImages(DirectoryInfo dir) => dir.GetFiles()
            .Where(f => ImageExts.Contains(f.Extension.ToLowerInvariant()))
            .OrderBy(f => f.Name)
            .Select(f => f.FullName)
            .ToList();
    }
}
