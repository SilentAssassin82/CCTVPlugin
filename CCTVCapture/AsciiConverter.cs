using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace CCTVCapture
{
    /// <summary>
    /// Converts images to ASCII art OR SE color characters using Space Engineers LCD characters
    /// </summary>
    public class AsciiConverter
    {
        // SE's hidden color palette base (512 colors, 9-bit RGB)
        private const int SE_COLOR_BASE = 0xE100;
        private const double BIT_SPACING = 255.0 / 7.0; // 36.428 - quantize 0-255 to 0-7

        // Whip's actual character ramp (optimized for SE LCD rendering)
        // Ordered by visual density/brightness as they appear on SE LCDs
        // These are carefully tested to produce smooth gradients
        private static readonly char[] WHIP_RAMP = new char[]
        {
            ' ', '·', '˙', '°', '░', '▒', '▓', '█'
        };

        // Alternative rich ramp with more gradations
        private static readonly char[] RICH_RAMP = new char[]
        {
            ' ', '`', '.', '·', '˙', '°', '░', '▒', '▓', '█'
        };

        // Fallback simple ramp
        private static readonly char[] BLOCK_RAMP = new char[]
        {
            ' ', '░', '▒', '▓', '█'
        };

        // Per-thread reusable channel buffers for Floyd-Steinberg dithering.
        // Avoids allocating three ~500 KB float arrays on the LOH on every frame.
        // [ThreadStatic] gives each thread pool thread its own copy so the two parallel
        // Task.Run conversions (181×181 and 362×362) never share the same arrays.
        [ThreadStatic]
        private static float[] _rChannelBuf;
        [ThreadStatic]
        private static float[] _gChannelBuf;
        [ThreadStatic]
        private static float[] _bChannelBuf;

        // Slight contrast boost for dark SE scenes (1.0 = no change, 1.3 = moderate boost)
        private const float CONTRAST = 1.2f;
        private const float CONTRAST_MIDPOINT = 0.5f;

        /// <summary>
        /// Apply contrast adjustment to a 0-255 value
        /// </summary>
        private static byte AdjustContrast(byte value)
        {
            float v = value / 255f;
            v = (v - CONTRAST_MIDPOINT) * CONTRAST + CONTRAST_MIDPOINT;
            int result = (int)(v * 255f + 0.5f);
            return (byte)(result < 0 ? 0 : (result > 255 ? 255 : result));
        }

        /// <summary>
        /// Apply post-processing filter to bitmap before ASCII conversion
        /// Uses fast LockBits for performance (~2-5ms overhead)
        /// </summary>
        public static Bitmap ApplyPostProcess(Bitmap image, CCTVCommon.PostProcessMode mode)
        {
            if (mode == CCTVCommon.PostProcessMode.None)
                return image;

            switch (mode)
            {
                case CCTVCommon.PostProcessMode.LightBlur:
                    return ApplyBoxBlur(image, 1);

                case CCTVCommon.PostProcessMode.MediumBlur:
                    return ApplyBoxBlur(image, 2);

                case CCTVCommon.PostProcessMode.Sharpen:
                    return ApplySharpen(image);

                default:
                    return image;
            }
        }

        /// <summary>
        /// Fast box blur using LockBits - smooths pixels, reduces harsh edges
        /// radius=1: 3x3 kernel (~2-3ms), radius=2: 5x5 kernel (~5-7ms)
        /// </summary>
        private static Bitmap ApplyBoxBlur(Bitmap source, int radius)
        {
            int width = source.Width;
            int height = source.Height;
            Bitmap result = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            BitmapData srcData = source.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            BitmapData dstData = result.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = srcData.Stride;
            byte[] srcPixels = new byte[stride * height];
            byte[] dstPixels = new byte[stride * height];
            Marshal.Copy(srcData.Scan0, srcPixels, 0, srcPixels.Length);

            int kernelSize = radius * 2 + 1;
            int kernelArea = kernelSize * kernelSize;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0;
                    int count = 0;

                    for (int ky = -radius; ky <= radius; ky++)
                    {
                        for (int kx = -radius; kx <= radius; kx++)
                        {
                            int px = x + kx;
                            int py = y + ky;

                            if (px >= 0 && px < width && py >= 0 && py < height)
                            {
                                int idx = py * stride + px * 3;
                                sumB += srcPixels[idx];
                                sumG += srcPixels[idx + 1];
                                sumR += srcPixels[idx + 2];
                                count++;
                            }
                        }
                    }

                    int dstIdx = y * stride + x * 3;
                    dstPixels[dstIdx] = (byte)(sumB / count);
                    dstPixels[dstIdx + 1] = (byte)(sumG / count);
                    dstPixels[dstIdx + 2] = (byte)(sumR / count);
                }
            }

            Marshal.Copy(dstPixels, 0, dstData.Scan0, dstPixels.Length);
            source.UnlockBits(srcData);
            result.UnlockBits(dstData);

            return result;
        }

        /// <summary>
        /// Sharpen filter using unsharp mask - enhances edges (~3-5ms)
        /// </summary>
        private static Bitmap ApplySharpen(Bitmap source)
        {
            int width = source.Width;
            int height = source.Height;
            Bitmap result = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            BitmapData srcData = source.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            BitmapData dstData = result.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = srcData.Stride;
            byte[] srcPixels = new byte[stride * height];
            byte[] dstPixels = new byte[stride * height];
            Marshal.Copy(srcData.Scan0, srcPixels, 0, srcPixels.Length);

            // 3x3 sharpen kernel
            int[,] kernel = new int[,]
            {
                {  0, -1,  0 },
                { -1,  5, -1 },
                {  0, -1,  0 }
            };

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0;

                    for (int ky = -1; ky <= 1; ky++)
                    {
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            int px = x + kx;
                            int py = y + ky;
                            int idx = py * stride + px * 3;
                            int weight = kernel[ky + 1, kx + 1];

                            sumB += srcPixels[idx] * weight;
                            sumG += srcPixels[idx + 1] * weight;
                            sumR += srcPixels[idx + 2] * weight;
                        }
                    }

                    int dstIdx = y * stride + x * 3;
                    dstPixels[dstIdx] = (byte)Math.Max(0, Math.Min(255, sumB));
                    dstPixels[dstIdx + 1] = (byte)Math.Max(0, Math.Min(255, sumG));
                    dstPixels[dstIdx + 2] = (byte)Math.Max(0, Math.Min(255, sumR));
                }
            }

            Marshal.Copy(dstPixels, 0, dstData.Scan0, dstPixels.Length);
            source.UnlockBits(srcData);
            result.UnlockBits(dstData);

            return result;
        }

        /// <summary>
        /// Convert RGB color to SE's internal color character (0xE100 range)
        /// </summary>
        public static char ColorToChar(byte r, byte g, byte b)
        {
            // Quantize to 3 bits per channel (0-7)
            int rInt = (int)Math.Round(r / BIT_SPACING);
            int gInt = (int)Math.Round(g / BIT_SPACING);
            int bInt = (int)Math.Round(b / BIT_SPACING);

            // Clamp to 0-7 range
            rInt = Math.Max(0, Math.Min(7, rInt));
            gInt = Math.Max(0, Math.Min(7, gInt));
            bInt = Math.Max(0, Math.Min(7, bInt));

            // Pack into 9-bit index: (R << 6) | (G << 3) | B
            int colorIndex = (rInt << 6) | (gInt << 3) | bInt;

            // Return character at 0xE100 + index
            return (char)(SE_COLOR_BASE + colorIndex);
        }

        /// <summary>
        /// Converts a bitmap to SE color characters using fast LockBits pixel access
        /// with contrast enhancement for dark SE scenes
        /// </summary>
        public static string ConvertToColorChars(Bitmap image, int targetWidth, int targetHeight)
        {
            // Resize image to target dimensions with high quality
            Bitmap resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, targetWidth, targetHeight);
            }

            // Fast pixel access via LockBits (50-100x faster than GetPixel)
            BitmapData bmpData = resized.LockBits(
                new Rectangle(0, 0, targetWidth, targetHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            IntPtr scan0 = bmpData.Scan0;
            int byteCount = stride * targetHeight;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(scan0, pixels, 0, byteCount);

            resized.UnlockBits(bmpData);

            // Convert to color characters with contrast boost
            StringBuilder result = new StringBuilder((targetWidth + 1) * targetHeight);

            for (int y = 0; y < targetHeight; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    // Format24bppRgb is BGR order
                    byte b = AdjustContrast(pixels[idx]);
                    byte g2 = AdjustContrast(pixels[idx + 1]);
                    byte r = AdjustContrast(pixels[idx + 2]);

                    result.Append(ColorToChar(r, g2, b));
                }

                if (y < targetHeight - 1)
                    result.Append('\n');
            }

            resized.Dispose();
            return result.ToString();
        }

        /// <summary>
        /// Converts a bitmap to SE color characters with Floyd-Steinberg dithering
        /// for smoother gradients and better color transitions
        /// </summary>
        public static string ConvertToColorCharsDithered(Bitmap image, int targetWidth, int targetHeight)
        {
            // Resize image to target dimensions with high quality
            Bitmap resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, targetWidth, targetHeight);
            }

            // Fast pixel access via LockBits
            BitmapData bmpData = resized.LockBits(
                new Rectangle(0, 0, targetWidth, targetHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            byte[] pixels = new byte[stride * targetHeight];
            Marshal.Copy(bmpData.Scan0, pixels, 0, pixels.Length);
            resized.UnlockBits(bmpData);
            resized.Dispose();

            // Convert to float RGB arrays for dithering with contrast.
            // Reuse per-thread buffers (flat row-major [y*w+x]) to avoid per-frame LOH pressure.
            int total = targetWidth * targetHeight;
            if (_rChannelBuf == null || _rChannelBuf.Length < total)
            {
                _rChannelBuf = new float[total];
                _gChannelBuf = new float[total];
                _bChannelBuf = new float[total];
            }
            else
            {
                Array.Clear(_rChannelBuf, 0, total);
                Array.Clear(_gChannelBuf, 0, total);
                Array.Clear(_bChannelBuf, 0, total);
            }
            float[] rChannel = _rChannelBuf;
            float[] gChannel = _gChannelBuf;
            float[] bChannel = _bChannelBuf;

            for (int y = 0; y < targetHeight; y++)
            {
                int rowOffset = y * stride;
                int rowBase = y * targetWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    // Apply contrast and normalize to 0-1 range
                    bChannel[rowBase + x] = AdjustContrast(pixels[idx]) / 255f;
                    gChannel[rowBase + x] = AdjustContrast(pixels[idx + 1]) / 255f;
                    rChannel[rowBase + x] = AdjustContrast(pixels[idx + 2]) / 255f;
                }
            }

            // Apply Floyd-Steinberg dithering to each RGB channel independently
            for (int y = 0; y < targetHeight; y++)
            {
                int rowBase = y * targetWidth;
                int nextRowBase = rowBase + targetWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    int i = rowBase + x;
                    float oldR = rChannel[i];
                    float oldG = gChannel[i];
                    float oldB = bChannel[i];

                    // Quantize to SE's 3-bit per channel (0-7 levels)
                    int newR = (int)Math.Round(oldR * 7f);
                    int newG = (int)Math.Round(oldG * 7f);
                    int newB = (int)Math.Round(oldB * 7f);

                    // Store quantized values
                    rChannel[i] = newR / 7f;
                    gChannel[i] = newG / 7f;
                    bChannel[i] = newB / 7f;

                    // Calculate quantization error for each channel
                    float errR = oldR - rChannel[i];
                    float errG = oldG - gChannel[i];
                    float errB = oldB - bChannel[i];

                    // Distribute error to neighboring pixels (Floyd-Steinberg weights)
                    if (x + 1 < targetWidth)
                    {
                        rChannel[i + 1] += errR * 7f / 16f;
                        gChannel[i + 1] += errG * 7f / 16f;
                        bChannel[i + 1] += errB * 7f / 16f;
                    }

                    if (y + 1 < targetHeight)
                    {
                        if (x > 0)
                        {
                            rChannel[nextRowBase + x - 1] += errR * 3f / 16f;
                            gChannel[nextRowBase + x - 1] += errG * 3f / 16f;
                            bChannel[nextRowBase + x - 1] += errB * 3f / 16f;
                        }

                        rChannel[nextRowBase + x] += errR * 5f / 16f;
                        gChannel[nextRowBase + x] += errG * 5f / 16f;
                        bChannel[nextRowBase + x] += errB * 5f / 16f;

                        if (x + 1 < targetWidth)
                        {
                            rChannel[nextRowBase + x + 1] += errR * 1f / 16f;
                            gChannel[nextRowBase + x + 1] += errG * 1f / 16f;
                            bChannel[nextRowBase + x + 1] += errB * 1f / 16f;
                        }
                    }

                    // Clamp to valid range
                    rChannel[i] = Math.Max(0f, Math.Min(1f, rChannel[i]));
                    gChannel[i] = Math.Max(0f, Math.Min(1f, gChannel[i]));
                    bChannel[i] = Math.Max(0f, Math.Min(1f, bChannel[i]));
                }
            }

            // Convert dithered RGB values to SE color characters
            StringBuilder result = new StringBuilder((targetWidth + 1) * targetHeight);

            for (int y = 0; y < targetHeight; y++)
            {
                int rowBase = y * targetWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    int i = rowBase + x;
                    byte r = (byte)(rChannel[i] * 255f);
                    byte g = (byte)(gChannel[i] * 255f);
                    byte b = (byte)(bChannel[i] * 255f);

                    result.Append(ColorToChar(r, g, b));
                }

                if (y < targetHeight - 1)
                    result.Append('\n');
            }

            return result.ToString();
        }

        /// <summary>
        /// Converts a bitmap to grayscale ASCII using fast LockBits pixel access
        /// with contrast enhancement
        /// </summary>
        public static string ConvertToAscii(Bitmap image, int targetWidth, int targetHeight, bool useBlockMode = true)
        {
            char[] charRamp = RICH_RAMP;

            // SE LCD characters are roughly 1:2 (width:height)
            int adjustedHeight = targetHeight / 2;

            // Resize image to target dimensions
            Bitmap resized = new Bitmap(targetWidth, adjustedHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, targetWidth, adjustedHeight);
            }

            // Fast pixel access via LockBits
            BitmapData bmpData = resized.LockBits(
                new Rectangle(0, 0, targetWidth, adjustedHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            IntPtr scan0 = bmpData.Scan0;
            int byteCount = stride * adjustedHeight;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(scan0, pixels, 0, byteCount);

            resized.UnlockBits(bmpData);

            // Convert to ASCII with contrast boost
            StringBuilder result = new StringBuilder((targetWidth + 1) * adjustedHeight);

            for (int y = 0; y < adjustedHeight; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    // Format24bppRgb is BGR order
                    float luminance = (pixels[idx + 2] * 0.299f + pixels[idx + 1] * 0.587f + pixels[idx] * 0.114f) / 255f;

                    // Apply contrast
                    luminance = (luminance - CONTRAST_MIDPOINT) * CONTRAST + CONTRAST_MIDPOINT;
                    luminance = luminance < 0f ? 0f : (luminance > 1f ? 1f : luminance);

                    int charIndex = (int)(luminance * (charRamp.Length - 1) + 0.5f);
                    charIndex = Math.Max(0, Math.Min(charRamp.Length - 1, charIndex));

                    result.Append(charRamp[charIndex]);
                }

                if (y < adjustedHeight - 1)
                    result.Append('\n');
            }

            resized.Dispose();
            return result.ToString();
        }

        /// <summary>
        /// Converts a bitmap to colored ASCII art for SE LCDs using fast LockBits
        /// </summary>
        public static string ConvertToColorAscii(Bitmap image, int targetWidth, int targetHeight, bool use16Colors)
        {
            char[] charRamp = RICH_RAMP;

            // SE LCD characters are roughly 1:2 (width:height)
            int adjustedHeight = targetHeight / 2;

            // Resize image with high quality
            Bitmap resized = new Bitmap(targetWidth, adjustedHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, targetWidth, adjustedHeight);
            }

            // Fast pixel access via LockBits
            BitmapData bmpData = resized.LockBits(
                new Rectangle(0, 0, targetWidth, adjustedHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            byte[] pixels = new byte[stride * adjustedHeight];
            Marshal.Copy(bmpData.Scan0, pixels, 0, pixels.Length);

            resized.UnlockBits(bmpData);

            // Convert to colored ASCII
            StringBuilder result = new StringBuilder((targetWidth * 30 + 1) * adjustedHeight);

            int lastR = -1, lastG = -1, lastB = -1;

            for (int y = 0; y < adjustedHeight; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    byte pb = AdjustContrast(pixels[idx]);
                    byte pg = AdjustContrast(pixels[idx + 1]);
                    byte pr = AdjustContrast(pixels[idx + 2]);

                    float luminance = (pr * 0.299f + pg * 0.587f + pb * 0.114f) / 255f;
                    int charIndex = (int)(luminance * (charRamp.Length - 1) + 0.5f);
                    charIndex = Math.Max(0, Math.Min(charRamp.Length - 1, charIndex));

                    int dr = pr, dg = pg, db = pb;
                    if (use16Colors)
                    {
                        dr = (int)Math.Round(pr / 85.0) * 85;
                        dg = (int)Math.Round(pg / 85.0) * 85;
                        db = (int)Math.Round(pb / 85.0) * 85;
                    }

                    if (dr != lastR || dg != lastG || db != lastB)
                    {
                        result.Append("<color=").Append(dr).Append(',').Append(dg).Append(',').Append(db).Append('>');
                        lastR = dr; lastG = dg; lastB = db;
                    }

                    result.Append(charRamp[charIndex]);
                }

                if (y < adjustedHeight - 1)
                {
                    result.Append('\n');
                    lastR = lastG = lastB = -1;
                }
            }

            resized.Dispose();
            return result.ToString();
        }

        /// <summary>
        /// Converts with Floyd-Steinberg dithering using fast LockBits
        /// </summary>
        public static string ConvertToAsciiDithered(Bitmap image, int targetWidth, int targetHeight)
        {
            int adjustedHeight = (int)(targetHeight * 0.55);

            Bitmap resized = new Bitmap(targetWidth, adjustedHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, targetWidth, adjustedHeight);
            }

            // Fast pixel access via LockBits
            BitmapData bmpData = resized.LockBits(
                new Rectangle(0, 0, targetWidth, adjustedHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            byte[] pixels = new byte[stride * adjustedHeight];
            Marshal.Copy(bmpData.Scan0, pixels, 0, pixels.Length);

            resized.UnlockBits(bmpData);
            resized.Dispose();

            // Convert to grayscale with contrast
            float[,] grayscale = new float[targetWidth, adjustedHeight];
            for (int y = 0; y < adjustedHeight; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    float lum = (pixels[idx + 2] * 0.299f + pixels[idx + 1] * 0.587f + pixels[idx] * 0.114f) / 255f;
                    lum = (lum - CONTRAST_MIDPOINT) * CONTRAST + CONTRAST_MIDPOINT;
                    grayscale[x, y] = lum < 0f ? 0f : (lum > 1f ? 1f : lum);
                }
            }

            // Apply Floyd-Steinberg dithering
            for (int y = 0; y < adjustedHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    float oldPixel = grayscale[x, y];

                    // Quantize to nearest level
                    int levels = RICH_RAMP.Length;
                    float newPixel = (float)Math.Round(oldPixel * (levels - 1)) / (levels - 1);
                    grayscale[x, y] = newPixel;

                    float error = oldPixel - newPixel;

                    // Distribute error to neighboring pixels
                    if (x + 1 < targetWidth)
                        grayscale[x + 1, y] += error * 7f / 16f;

                    if (y + 1 < adjustedHeight)
                    {
                        if (x > 0)
                            grayscale[x - 1, y + 1] += error * 3f / 16f;

                        grayscale[x, y + 1] += error * 5f / 16f;

                        if (x + 1 < targetWidth)
                            grayscale[x + 1, y + 1] += error * 1f / 16f;
                    }

                    // Clamp values
                    grayscale[x, y] = Math.Max(0, Math.Min(1, grayscale[x, y]));
                }
            }

            // Convert to ASCII
            StringBuilder result = new StringBuilder(targetWidth * adjustedHeight + adjustedHeight);

            for (int y = 0; y < adjustedHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    float brightness = grayscale[x, y];
                    int charIndex = (int)(brightness * (RICH_RAMP.Length - 1) + 0.5f);
                    charIndex = Math.Max(0, Math.Min(RICH_RAMP.Length - 1, charIndex));

                    result.Append(RICH_RAMP[charIndex]);
                }

                if (y < adjustedHeight - 1)
                    result.Append('\n');
            }

            return result.ToString();
        }

        /// <summary>
        /// GZip-compresses ASCII art and encodes as base64 for compact transmission.
        /// Receiver must detect the GZ mode suffix and decompress accordingly.
        /// </summary>
        public static string CompressAscii(string ascii)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(ascii);
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
                {
                    gz.Write(bytes, 0, bytes.Length);
                }
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        /// <summary>
        /// Decompresses a GZip+base64 encoded ASCII art string.
        /// </summary>
        public static string DecompressAscii(string base64)
        {
            byte[] compressed = Convert.FromBase64String(base64);
            using (var ms = new MemoryStream(compressed))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                gz.CopyTo(outMs);
                return Encoding.UTF8.GetString(outMs.ToArray());
            }
        }
    }
}
