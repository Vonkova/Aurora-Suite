using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AuroraSuite
{
    /// <summary>Which protocol the Sync tab uses to talk to the console.</summary>
    public enum TransportKind
    {
        Ftp,
        Xbdm
    }

    /// <summary>
    /// What the Sync tab needs from a console, independent of how it gets there.
    ///
    /// FTP and XBDM differ in more than just the socket: they use different path styles
    /// (/Hdd1/Aurora_0.7b/... vs Hdd:\Aurora_0.7b\...), so path handling belongs to the
    /// transport rather than the sync logic.
    /// </summary>
    public interface IConsoleTransport : IDisposable
    {
        /// <summary>Short name for logs, e.g. "FTP" or "XBDM".</summary>
        string Name { get; }

        /// <summary>Opens the connection. For XBDM there is no login step at all.</summary>
        void Connect();

        /// <summary>
        /// Turns the configured GameData path into whatever this transport actually wants.
        /// Call after Connect - XBDM checks the path against the console's real drive list.
        /// </summary>
        string ResolveBasePath(string configuredPath);

        string CombinePath(string basePath, string child);

        List<string> ListDirectoryNames(string remoteDir);

        HashSet<string> ListFileNames(string remoteDir);

        void UploadFile(string localPath, string remoteDir, string remoteFileName);

        /// <summary>Size of a remote file, or null if it can't be determined.</summary>
        long? GetFileSize(string remoteDir, string remoteFileName);

        void Quit();
    }

    /// <summary>
    /// Path translation between Aurora's FTP style and XBDM's native style.
    ///
    ///     FTP :  /Hdd1/Aurora_0.7b/Data/GameData
    ///     XBDM:  Hdd:\Aurora_0.7b\Data\GameData
    ///
    /// The leading segment is a device, and the two protocols spell it differently ("Hdd1"
    /// over FTP vs the "Hdd" that drivelist reports). Rather than hardcode a mapping, the
    /// device is resolved against the drive names the console actually reports, so an
    /// unusual setup still lines up.
    /// </summary>
    public static class XbdmPath
    {
        /// <summary>True if this already looks like an XBDM path (has a "Drive:" prefix).</summary>
        public static bool IsXbdmStyle(string path) =>
            !string.IsNullOrWhiteSpace(path) && path.Contains(':');

        public static string Combine(string basePath, string child) =>
            basePath.TrimEnd('\\') + "\\" + child.Trim('\\');

        /// <summary>
        /// Converts a configured path into an XBDM path. Accepts either style, so a user who
        /// already typed Hdd:\... gets it left alone.
        /// </summary>
        /// <param name="drives">Drive names from drivelist. May be empty; then the device is used as typed.</param>
        public static string FromConfigured(string configuredPath, IReadOnlyCollection<string> drives)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new ArgumentException("The Aurora GameData path is empty.");

            var path = configuredPath.Trim();

            // Already XBDM-native: just normalise the separators.
            if (IsXbdmStyle(path))
                return path.Replace('/', '\\').TrimEnd('\\');

            var segments = path.Replace('\\', '/')
                               .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                throw new ArgumentException($"'{configuredPath}' doesn't contain a device to map to an XBDM drive.");

            var device = segments[0];
            var rest = segments.Skip(1);

            var resolved = ResolveDrive(device, drives);
            var tail = string.Join("\\", rest);
            return tail.Length == 0 ? $"{resolved}:" : $"{resolved}:\\{tail}";
        }

        /// <summary>
        /// Matches an FTP device name against the console's drive list: exact first, then
        /// with trailing digits removed ("Hdd1" -> "Hdd"). Returns the console's own
        /// spelling when matched.
        /// </summary>
        public static string ResolveDrive(string device, IReadOnlyCollection<string> drives)
        {
            if (drives == null || drives.Count == 0)
                return device;

            var exact = drives.FirstOrDefault(d => string.Equals(d, device, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var trimmed = device.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (trimmed.Length > 0)
            {
                var loose = drives.FirstOrDefault(d => string.Equals(d, trimmed, StringComparison.OrdinalIgnoreCase));
                if (loose != null) return loose;
            }

            throw new IOException(
                $"'{device}' doesn't match any drive on the console. XBDM reports: " +
                string.Join(", ", drives) + ". Set the GameData path to use one of those " +
                $"(for example {drives.First()}:\\Aurora_0.7b\\Data\\GameData).");
        }
    }

    /// <summary>The original FTP path, unchanged - this is what the tool has always used.</summary>
    public sealed class FtpTransport : IConsoleTransport
    {
        private readonly Settings _settings;
        private readonly Action<string> _log;
        private readonly MiniFtpClient _ftp = new();

        public FtpTransport(Settings settings, Action<string> log)
        {
            _settings = settings;
            _log = log;
        }

        public string Name => "FTP";

        public void Connect()
        {
            _log($"Connecting to {_settings.Ip}:{_settings.Port} over FTP ...");
            _ftp.Connect(_settings.Ip, _settings.Port, _settings.Username, _settings.Password);
            _log("Connected and logged in.");
        }

        public string ResolveBasePath(string configuredPath) => configuredPath;

        public string CombinePath(string basePath, string child) => basePath.TrimEnd('/') + "/" + child;

        public List<string> ListDirectoryNames(string remoteDir)
        {
            // NLST first, LIST as a fallback - same order and behaviour as before.
            try
            {
                var names = _ftp.NlstNames(remoteDir);
                _log($"NLST returned {names.Count} entr(y/ies).");
                return names;
            }
            catch (Exception ex)
            {
                _log($"NLST failed ({ex.Message}), falling back to LIST...");
                return _ftp.ListDirectory(remoteDir).Where(e => e.IsDirectory).Select(e => e.Name).ToList();
            }
        }

        public HashSet<string> ListFileNames(string remoteDir) =>
            _ftp.ListDirectory(remoteDir)
                .Where(e => !e.IsDirectory)
                .Select(e => e.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public void UploadFile(string localPath, string remoteDir, string remoteFileName)
        {
            _ftp.ChangeDirectory(remoteDir);
            _ftp.UploadFile(localPath, remoteFileName);
        }

        public long? GetFileSize(string remoteDir, string remoteFileName)
        {
            _ftp.ChangeDirectory(remoteDir);
            return _ftp.GetSize(remoteFileName);
        }

        public void Quit()
        {
            try { _ftp.Quit(); } catch { /* best effort */ }
        }

        public void Dispose() => _ftp.Dispose();
    }

    /// <summary>
    /// Uploads over XBDM instead of FTP.
    ///
    /// The whole point: XBDM has no authentication, so no username or password is needed or
    /// even possible. If the console answers on port 730, it will accept the transfer.
    /// </summary>
    public sealed class XbdmTransport : IConsoleTransport
    {
        private readonly Settings _settings;
        private readonly Action<string> _log;
        private readonly XbdmClient _xbdm = new();
        private List<string> _drives = new();

        public XbdmTransport(Settings settings, Action<string> log)
        {
            _settings = settings;
            _log = log;
        }

        public string Name => "XBDM";

        /// <summary>XBDM target if one is set, otherwise the same address FTP uses.</summary>
        private string Target =>
            string.IsNullOrWhiteSpace(_settings.XbdmTarget) ? _settings.Ip : _settings.XbdmTarget.Trim();

        public void Connect()
        {
            _log($"Connecting to {Target}:{_settings.XbdmPort} over XBDM (no login required) ...");
            _xbdm.Connect(Target, _settings.XbdmPort);
            _log("Connected.");

            try
            {
                _drives = _xbdm.DriveList();
                _log($"Console drives: {string.Join(", ", _drives)}");
            }
            catch (Exception ex)
            {
                // Not fatal - the path just won't get sanity-checked against real drives.
                _log($"Could not read the drive list ({ex.Message}); using the path as configured.");
                _drives = new List<string>();
            }
        }

        public string ResolveBasePath(string configuredPath)
        {
            var resolved = XbdmPath.FromConfigured(configuredPath, _drives);
            if (!string.Equals(resolved, configuredPath, StringComparison.OrdinalIgnoreCase))
                _log($"Path '{configuredPath}' -> '{resolved}' for XBDM.");
            return resolved;
        }

        public string CombinePath(string basePath, string child) => XbdmPath.Combine(basePath, child);

        public List<string> ListDirectoryNames(string remoteDir)
        {
            var entries = _xbdm.DirList(remoteDir, out var found);
            if (!found)
                throw new IOException($"'{remoteDir}' does not exist on the console.");
            return entries.Where(e => e.IsDirectory).Select(e => e.Name).ToList();
        }

        public HashSet<string> ListFileNames(string remoteDir) =>
            _xbdm.DirList(remoteDir)
                 .Where(e => !e.IsDirectory)
                 .Select(e => e.Name)
                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public void UploadFile(string localPath, string remoteDir, string remoteFileName) =>
            _xbdm.SendFile(localPath, XbdmPath.Combine(remoteDir, remoteFileName));

        /// <summary>
        /// Reads the size back from a directory listing rather than getfileattributes -
        /// that command reports 0 on some setups, which would make every verify look like a
        /// failed transfer.
        /// </summary>
        public long? GetFileSize(string remoteDir, string remoteFileName)
        {
            try
            {
                var entry = _xbdm.DirList(remoteDir)
                                 .FirstOrDefault(e => string.Equals(e.Name, remoteFileName, StringComparison.OrdinalIgnoreCase));
                return entry?.Size;
            }
            catch
            {
                return null;   // treated as "uploaded but unverified"
            }
        }

        public void Quit() => _xbdm.Bye();

        public void Dispose() => _xbdm.Dispose();
    }

    public static class TransportFactory
    {
        public static IConsoleTransport Create(Settings settings, Action<string> log) =>
            settings.Transport == TransportKind.Xbdm
                ? new XbdmTransport(settings, log)
                : new FtpTransport(settings, log);
    }
}
