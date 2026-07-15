using System;
using System.Collections.Generic;
using System.IO;

namespace AuroraSuite
{
    /// <summary>
    /// Packs one or more encoded texture entries into a single Aurora .asset
    /// container. This is a direct, byte-for-byte port of build_asset_container()
    /// from aurora_asset_tool.py / aurora_screenshot_tool.py - including the
    /// (intentional, verified-working) quirk where the padding block is a full
    /// extra 0x800 bytes even when the header table already lands on a 0x800
    /// boundary. Do not "simplify" that away; it must match what the real
    /// AuroraAssetEditor-produced files look like.
    /// </summary>
    public static class AssetContainerBuilder
    {
        public const int AssetMax = 25; // AssetType.Max = ScreenshotEnd = ScreenshotStart(5) + 20
        public const uint Magic = 0x52584541;
        public const uint Version = 1;

        public const int TypeIcon = 0;
        public const int TypeBoxart = 2;
        public const int TypeBackground = 4;
        public const int TypeScreenshotStart = 5;
        public const int TypeScreenshotEnd = TypeScreenshotStart + 20; // inclusive, 20 slots: 5..24

        public static byte[] Build(SortedDictionary<int, (byte[] Header, byte[] Video)> entries)
        {
            uint flags = 0;
            uint screenshotCount = 0;
            uint dataSize = 0;
            var offsets = new Dictionary<int, uint>();
            uint running = 0;

            foreach (var kv in entries)
            {
                int idx = kv.Key;
                var (header, video) = kv.Value;
                if (header.Length != 52)
                    throw new Exception($"texture header must be 52 bytes, got {header.Length}");

                offsets[idx] = running;
                running += (uint)video.Length;
                dataSize += (uint)video.Length;
                flags |= (uint)(1 << idx);
                if (idx >= TypeScreenshotStart && idx <= TypeScreenshotEnd)
                    screenshotCount++;
            }

            using var ms = new MemoryStream();
            WriteBE(ms, Magic);
            WriteBE(ms, Version);
            WriteBE(ms, dataSize);
            WriteBE(ms, flags);
            WriteBE(ms, screenshotCount);

            for (int i = 0; i < AssetMax; i++)
            {
                if (entries.TryGetValue(i, out var e))
                {
                    WriteBE(ms, offsets[i]);
                    WriteBE(ms, (uint)e.Video.Length);
                    WriteBE(ms, 0u);
                    ms.Write(e.Header, 0, 52);
                }
                else
                {
                    WriteBE(ms, 0u);
                    WriteBE(ms, 0u);
                    WriteBE(ms, 0u);
                    ms.Write(new byte[52], 0, 52);
                }
            }

            // Matches the python tool exactly: pad_len = 0x800 - (len % 0x800), which
            // deliberately adds a FULL extra 0x800 block when already aligned.
            int padLen = 0x800 - (int)(ms.Length % 0x800);
            ms.Write(new byte[padLen], 0, padLen);

            foreach (var kv in entries)
                ms.Write(kv.Value.Video, 0, kv.Value.Video.Length);

            return ms.ToArray();
        }

        private static void WriteBE(Stream s, uint value)
        {
            s.WriteByte((byte)(value >> 24));
            s.WriteByte((byte)(value >> 16));
            s.WriteByte((byte)(value >> 8));
            s.WriteByte((byte)value);
        }
    }
}
