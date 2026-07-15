using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AuroraSuite
{
    /// <summary>
    /// Direct port of the Python tool's cover_resize() / image_to_raw_argb().
    /// Uses System.Drawing (GDI+) since this only ever runs on Windows.
    /// </summary>
    public static class ImageProcessing
    {
        /// <summary>
        /// Resize + center-crop so the image exactly fills target dimensions,
        /// like CSS background-size:cover. Caller owns disposal of the result.
        /// </summary>
        public static Bitmap CoverResize(Bitmap src, int targetW, int targetH)
        {
            int srcW = src.Width, srcH = src.Height;
            double scale = Math.Max((double)targetW / srcW, (double)targetH / srcH);
            int newW = (int)Math.Round(srcW * scale);
            int newH = (int)Math.Round(srcH * scale);

            using var resized = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, 0, 0, newW, newH);
            }

            int left = (newW - targetW) / 2;
            int top = (newH - targetH) / 2;

            var cropped = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(resized,
                    new Rectangle(0, 0, targetW, targetH),
                    new Rectangle(left, top, targetW, targetH),
                    GraphicsUnit.Pixel);
            }

            return cropped;
        }

        /// <summary>
        /// Matches AuroraAsset.cs / the python tool's ImageToRawArgb: per-pixel bytes
        /// are A,R,G,B, row-major. GDI+'s Format32bppArgb stores each pixel in memory
        /// as B,G,R,A (little-endian 0xAARRGGBB), so the bytes are reordered here.
        /// </summary>
        public static byte[] ImageToRawArgb(Bitmap img)
        {
            int w = img.Width, h = img.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = img.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var outBytes = new byte[w * h * 4];
                var rowBuf = new byte[Math.Abs(data.Stride)];
                int outIdx = 0;

                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        data.Scan0 + y * data.Stride, rowBuf, 0, rowBuf.Length);

                    for (int x = 0; x < w; x++)
                    {
                        int srcIdx = x * 4;
                        byte b = rowBuf[srcIdx + 0];
                        byte g = rowBuf[srcIdx + 1];
                        byte r = rowBuf[srcIdx + 2];
                        byte a = rowBuf[srcIdx + 3];

                        outBytes[outIdx++] = a;
                        outBytes[outIdx++] = r;
                        outBytes[outIdx++] = g;
                        outBytes[outIdx++] = b;
                    }
                }

                return outBytes;
            }
            finally
            {
                img.UnlockBits(data);
            }
        }

        /// <summary>Loads an image file, throwing if it's corrupt/unreadable/truncated.</summary>
        public static Bitmap LoadValidImage(string path)
        {
            // Bitmap's constructor defers decoding for some codecs; force a full
            // decode by cloning into a fresh 32bppArgb bitmap so truncated/corrupt
            // pixel data surfaces here rather than later during resize/LockBits.
            using var raw = new Bitmap(path);
            var clone = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(clone))
                g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
            return clone;
        }
    }
}
