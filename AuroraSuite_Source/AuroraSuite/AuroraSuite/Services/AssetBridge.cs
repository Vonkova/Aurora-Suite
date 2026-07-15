using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace AuroraSuite
{
    /// <summary>
    /// Runs the native Aurora texture encoder (bridge.exe + AuroraAsset.dll +
    /// msvcr100.dll) exactly the way the Python aurora_asset_tool.py / bridge.c pair
    /// did - bridge.exe is a real Win32 program that calls AuroraAsset.dll's own
    /// ConvertImageToAsset export, so the output is byte-for-byte what
    /// AuroraAssetEditor itself would produce. This class just replaces the Python
    /// process launcher with a direct C# one; the actual encoding still happens
    /// inside the same native DLL, nothing about the texture format itself is
    /// reimplemented here.
    /// </summary>
    public static class AssetBridge
    {
        private static string? _bridgeDir;

        /// <summary>
        /// Extracts the three embedded native files into a stable per-user folder
        /// (once) and returns that folder. Safe to call repeatedly.
        /// </summary>
        public static string EnsureBridgeExtracted()
        {
            if (_bridgeDir != null && File.Exists(Path.Combine(_bridgeDir, "bridge.exe")))
                return _bridgeDir;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuroraSuite", "bridge");
            Directory.CreateDirectory(dir);

            ExtractIfMissing("AuroraSuite.Assets.bridge.exe", Path.Combine(dir, "bridge.exe"));
            ExtractIfMissing("AuroraSuite.Assets.AuroraAsset.dll", Path.Combine(dir, "AuroraAsset.dll"));
            ExtractIfMissing("AuroraSuite.Assets.msvcr100.dll", Path.Combine(dir, "msvcr100.dll"));

            _bridgeDir = dir;
            return dir;
        }

        private static void ExtractIfMissing(string resourceName, string destPath)
        {
            if (File.Exists(destPath)) return;

            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new Exception($"Embedded resource '{resourceName}' not found - the bridge tooling wasn't packaged into this build.");

            using var fs = File.Create(destPath);
            stream.CopyTo(fs);
        }

        /// <summary>
        /// Encodes one raw ARGB image into an Aurora texture header (52 bytes) +
        /// video (compressed/raw pixel) blob, matching bridge.c's "encode" mode:
        ///   bridge.exe encode _in.raw width height useCompression header.bin video.bin
        /// Calls are made sequentially against fixed filenames in the bridge folder
        /// (mirrors the Python tool's approach exactly), so this method is NOT safe
        /// to call concurrently from multiple threads - callers must serialize.
        /// </summary>
        public static (byte[] Header, byte[] Video) Encode(byte[] rawArgb, int width, int height, bool useCompression)
        {
            var dir = EnsureBridgeExtracted();

            var inPath = Path.Combine(dir, "_in.raw");
            var hdrPath = Path.Combine(dir, "_out_header.bin");
            var vidPath = Path.Combine(dir, "_out_video.bin");

            File.WriteAllBytes(inPath, rawArgb);
            if (File.Exists(hdrPath)) File.Delete(hdrPath);
            if (File.Exists(vidPath)) File.Delete(vidPath);

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(dir, "bridge.exe"),
                WorkingDirectory = dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("encode");
            psi.ArgumentList.Add("_in.raw");
            psi.ArgumentList.Add(width.ToString());
            psi.ArgumentList.Add(height.ToString());
            psi.ArgumentList.Add(useCompression ? "1" : "0");
            psi.ArgumentList.Add("_out_header.bin");
            psi.ArgumentList.Add("_out_video.bin");

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0 || !File.Exists(hdrPath) || !File.Exists(vidPath))
            {
                throw new Exception(
                    $"bridge.exe encode failed (exit {proc.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
            }

            return (File.ReadAllBytes(hdrPath), File.ReadAllBytes(vidPath));
        }
    }
}
