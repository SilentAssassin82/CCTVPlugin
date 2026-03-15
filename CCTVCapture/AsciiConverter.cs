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

        // ASCII character ramp ordered by visual density on SE LCDs.
        // 11 levels for finer gradation; all standard ASCII (1 byte UTF-8).
        private static readonly char[] WHIP_RAMP = new char[]
        {
            ' ', '.', '*', '!', 'v', 'n', 'z', 'm', '#', 'W', '@'
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
 

        // Pre-normalised 4×4 Bayer ordered-dither matrix.
        // Raw Bayer values (0-15) are divided by 16 then centred on zero,
        // giving a range of [-0.5, +0.4375].  Applied per-pixel as:
        //   brightness += _bayerNorm[y & 3, x & 3] / (rampLevels - 1)
        // which shifts each pixel by ±(0.5 / rampLevels) before rounding —
        // exactly one half-step — so neighbouring positions round to different
        // levels.  Because the matrix is position-fixed, static parts of the
        // video produce the same halftone pattern on every frame (no flicker).
        private static readonly float[,] _bayerNorm =
        {
            { -0.5000f,  0.0000f, -0.3750f,  0.1250f },
            {  0.2500f, -0.2500f,  0.3750f, -0.1250f },
            { -0.3125f,  0.1875f, -0.4375f,  0.0625f },
            {  0.4375f, -0.0625f,  0.3125f, -0.1875f }
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

        // Per-thread reusable grayscale buffer for Floyd-Steinberg dithering (grayscale path).
        // Replaces the per-frame float[,] allocation that landed on the LOH every frame.
        [ThreadStatic]
        private static float[] _grayBuf;

        // Per-thread reusable pixel buffer for LockBits reads.
        // Avoids allocating a new byte[] (up to ~400 KB for 362×362) every frame.
        [ThreadStatic]
        private static byte[] _pixelBuf;

        // Per-thread reusable StringBuilder for ASCII/color output.
        // Avoids a new ~260 KB StringBuilder backing array per frame.
        [ThreadStatic]
        private static StringBuilder _sbBuf;

        // Per-thread reusable MemoryStream for GZip compression.
        // Avoids per-frame MemoryStream + internal byte[] allocations on the LOH.
        [ThreadStatic]
        private static MemoryStream _compressMs;

        // Per-thread reusable byte[] for UTF-8 encoding in CompressAscii.
        // The 0xE100 color chars are 3 bytes each in UTF-8, so 362×362 ≈ 393 KB.
        [ThreadStatic]
        private static byte[] _utf8Buf;

        /// <summary>
        /// Returns (and possibly grows) the per-thread pixel buffer.
        /// </summary>
        private static byte[] RentPixelBuf(int minSize)
        {
            if (_pixelBuf == null || _pixelBuf.Length < minSize)
                _pixelBuf = new byte[minSize];
            return _pixelBuf;
        }

        /// <summary>
        /// Returns (and possibly grows) the per-thread StringBuilder, cleared to empty.
        /// </summary>
        private static StringBuilder RentStringBuilder(int capacity)
        {
            if (_sbBuf == null || _sbBuf.Capacity < capacity)
                _sbBuf = new StringBuilder(capacity);
            else
                _sbBuf.Clear();
            return _sbBuf;
        }

        // Bayer dither amplitude for the colour path
        // Values below 1.0 compress the threshold range toward 0.5, so only pixels whose
        // channel value is within (SCALE × 0.5) of a quantisation boundary get dithered.
        // This reduces the visible 4×4 halftone pattern on smooth near-neutral areas
        // (e.g. slightly cool sky) while still smoothing genuine colour transitions.
        // 0.6 → threshold window ±0.28 step; pixels solidly inside a level stay put.
        private const float BAYER_COLOR_SCALE = 0.6f;

        // Contrast boost applied to grayscale paths only (1.0 = no change, 1.25 = balanced NV look).
        // NOT used for colour paths — boosting contrast before 3-bit quantisation pushes more
        // pixels to 0 or 7, producing blocky colour artifacts on SE LCDs.
        private const float CONTRAST = 1.25f;
        private const float CONTRAST_MIDPOINT = 0.5f;

        // Gamma applied to luminance BEFORE contrast in grayscale paths.
        // Values < 1.0 lift shadows; values closer to 1.0 crush blacks harder.
        // 0.8 recovers background detail in dark SE scenes while still keeping
        // darker areas visibly darker than lit areas for NV definition.
        private const float GRAYSCALE_GAMMA = 0.8f;

        // Approximate visual density of each WHIP_RAMP character on SE LCDs
        // (0 = empty, 1 = full block).  Block elements ░▒▓█ are standardised at
        // 25%/50%/75%/100% fill; dot characters ·˙° are estimated from their
        // rendered glyph area at typical LCD font sizes.
        // These drive the density-linearized LUTs below so that equal brightness
        // intervals produce equal apparent density changes on the LCD — eliminating
        // the visible banding artefact at the °→░ transition caused by linear mapping.
        private static readonly float[] WHIP_DENSITY =
            { 0.00f, 0.04f, 0.10f, 0.15f, 0.25f, 0.35f, 0.45f, 0.55f, 0.65f, 0.78f, 0.92f };

        // LUT: raw grayscale byte → gamma-lifted + contrast-boosted byte.
        // Applies the same shadow lift as the grayscale ASCII paths but outputs
        // back to 0–255 for the color char encoder to read.  Used inside
        // DesaturateBitmap so NV / desaturated frames benefit from the high-gain
        // lift before 3-bit colour quantisation (safe because the image is already
        // monochrome — no blocky colour artifacts).
        private static readonly byte[] _grayLiftLUT = BuildGrayLiftLUT();

        // LUT: raw grayscale byte [0-255] → density-linearized float [0,1].
        // Folds gamma lift, contrast boost, and density-curve linearisation into a
        // single table.  In the output space each WHIP_RAMP character occupies an
        // equal 1/(N-1) interval, so standard Bayer and Floyd-Steinberg dithering
        // with uniform quantisation levels produce perceptually correct halftones.
        private static readonly float[] _grayToLinear = BuildGrayToLinearLUT();

        // LUT: raw grayscale byte → WHIP_RAMP char index (undithered path only).
        // Equivalent to _grayToLinear followed by round-to-nearest-index.
        private static readonly int[] _grayToCharIdx = BuildGrayToCharIdxLUT();

        /// <summary>
        /// Maps brightness [0,1] (after gamma+contrast) into a density-linearized
        /// [0,1] space where each WHIP_RAMP character occupies an equal interval.
        /// Interpolates linearly between the known density anchors.
        /// </summary>
        private static float DensityLinearize(float brightness)
        {
            int len = WHIP_DENSITY.Length;
            if (brightness <= WHIP_DENSITY[0]) return 0f;
            if (brightness >= WHIP_DENSITY[len - 1]) return 1f;

            for (int j = 1; j < len; j++)
            {
                if (brightness <= WHIP_DENSITY[j])
                {
                    float t = (brightness - WHIP_DENSITY[j - 1])
                            / (WHIP_DENSITY[j] - WHIP_DENSITY[j - 1]);
                    return ((j - 1) + t) / (len - 1);
                }
            }
            return 1f;
        }

        private static byte[] BuildGrayLiftLUT()
        {
            byte[] lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                float lv = (float)Math.Pow(i / 255f, GRAYSCALE_GAMMA);
                lv = (lv - CONTRAST_MIDPOINT) * CONTRAST + CONTRAST_MIDPOINT;
                lv = lv < 0f ? 0f : (lv > 1f ? 1f : lv);
                lut[i] = (byte)(lv * 255f + 0.5f);
            }
            return lut;
        }

        private static float[] BuildGrayToLinearLUT()
        {
            float[] lut = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float lv = (float)Math.Pow(i / 255f, GRAYSCALE_GAMMA);
                lv = (lv - CONTRAST_MIDPOINT) * CONTRAST + CONTRAST_MIDPOINT;
                lv = lv < 0f ? 0f : (lv > 1f ? 1f : lv);
                lut[i] = DensityLinearize(lv);
            }
            return lut;
        }

        private static int[] BuildGrayToCharIdxLUT()
        {
            int levels = WHIP_RAMP.Length;
            float scale = levels - 1;
            int[] lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                int idx = (int)(_grayToLinear[i] * scale + 0.5f);
                if (idx >= levels) idx = levels - 1;
                lut[i] = idx;
            }
            return lut;
        }

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
        /// Desaturates a bitmap in-place using fast LockBits.
        /// When nightVision is false: sets R=G=B=luminance (neutral grayscale).
        /// When nightVision is true: maps luminance to a green NV phosphor gradient —
        ///   dark → black, mid → green (R=0,G=lum,B=0), bright → white-green washout.
        /// The tint is baked into pixel RGB because SE font tint is ignored for color chars.
        /// </summary>
        public static void DesaturateBitmap(Bitmap image, bool nightVision = false)
        {
            int width = image.Width;
            int height = image.Height;

            BitmapData data = image.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadWrite,
                PixelFormat.Format24bppRgb);

            int stride = data.Stride;
            byte[] pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x * 3;
                    // Format24bppRgb is BGR order
                    byte gray = (byte)(pixels[idx + 2] * 0.299f + pixels[idx + 1] * 0.587f + pixels[idx] * 0.114f);

                    // High-gain lift: same gamma + contrast as grayscale ASCII paths.
                    // Safe here because the image is already monochrome (neutral gray
                    // or NV green) so the boost won't produce blocky colour artifacts.
                    gray = _grayLiftLUT[gray];

                    if (nightVision)
                    {
                        // NV phosphor gradient: luminance drives green channel primarily.
                        // Below ~60% brightness: R and B stay near zero → pure green.
                        // Above ~60%: R and B ramp toward 255 → hot-spot washout to white-green.
                        // This mimics real NV tube bloom on bright objects.
                        float lum = gray / 255f;
                        byte g2 = gray; // Green tracks luminance 1:1
                        byte r, b;
                        if (lum <= 0.6f)
                        {
                            r = 0;
                            b = 0;
                        }
                        else
                        {
                            // Ramp R and B from 0 at 60% to 255 at 100%
                            float washout = (lum - 0.6f) / 0.4f;
                            byte wb = (byte)(washout * 255f + 0.5f);
                            r = wb;
                            b = wb;
                        }
                        pixels[idx]     = b; // B
                        pixels[idx + 1] = g2; // G
                        pixels[idx + 2] = r; // R
                    }
                    else
                    {
                        pixels[idx]     = gray; // B
                        pixels[idx + 1] = gray; // G
                        pixels[idx + 2] = gray; // R
                    }
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            image.UnlockBits(data);
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
                    int totalWeight = 0;

                    for (int ky = -radius; ky <= radius; ky++)
                    {
                        for (int kx = -radius; kx <= radius; kx++)
                        {
                            int px = x + kx;
                            int py = y + ky;

                            if (px >= 0 && px < width && py >= 0 && py < height)
                            {
                                // Gaussian-weighted for radius==1 (LightBlur): centre=4, cross=2, corners=1
                                // Falls back to equal weighting (box blur) for radius > 1
                                int dist = Math.Abs(kx) + Math.Abs(ky);
                                int weight = (radius == 1) ? (dist == 0 ? 4 : dist == 1 ? 2 : 1) : 1;
                                int idx = py * stride + px * 3;
                                sumB += srcPixels[idx] * weight;
                                sumG += srcPixels[idx + 1] * weight;
                                sumR += srcPixels[idx + 2] * weight;
                                totalWeight += weight;
                            }
                        }
                    }

                    int dstIdx = y * stride + x * 3;
                    dstPixels[dstIdx] = (byte)(sumB / totalWeight);
                    dstPixels[dstIdx + 1] = (byte)(sumG / totalWeight);
                    dstPixels[dstIdx + 2] = (byte)(sumR / totalWeight);
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
            int byteCount = stride * targetHeight;
            byte[] pixels = RentPixelBuf(byteCount);
            Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);

            resized.UnlockBits(bmpData);

            // Convert to color characters with contrast boost
            StringBuilder result = RentStringBuilder((targetWidth + 1) * targetHeight);

            for (int y = 0; y < targetHeight; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    // Format24bppRgb is BGR order. No contrast adjustment — see CONTRAST comment.
                    byte b = pixels[idx];
                    byte g2 = pixels[idx + 1];
                    byte r = pixels[idx + 2];

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
            int pixelBytes = stride * targetHeight;
            byte[] pixels = RentPixelBuf(pixelBytes);
            Marshal.Copy(bmpData.Scan0, pixels, 0, pixelBytes);
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
                    // Normalize to 0-1 range. No contrast adjustment — see CONTRAST comment.
                    bChannel[rowBase + x] = pixels[idx] / 255f;
                    gChannel[rowBase + x] = pixels[idx + 1] / 255f;
                    rChannel[rowBase + x] = pixels[idx + 2] / 255f;
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

                    // Distribute error to neighboring pixels (Floyd-Steinberg weights).
                    // DITHER_STRENGTH < 1.0 reduces grain from SE's coarse 8-level palette
                    // without disabling color smoothing entirely (0.75 = good balance).
                    const float DITHER_STRENGTH = 1.0f;
                    if (x + 1 < targetWidth)
                    {
                        rChannel[i + 1] += errR * (7f / 16f) * DITHER_STRENGTH;
                        gChannel[i + 1] += errG * (7f / 16f) * DITHER_STRENGTH;
                        bChannel[i + 1] += errB * (7f / 16f) * DITHER_STRENGTH;
                    }

                    if (y + 1 < targetHeight)
                    {
                        if (x > 0)
                        {
                            rChannel[nextRowBase + x - 1] += errR * (3f / 16f) * DITHER_STRENGTH;
                            gChannel[nextRowBase + x - 1] += errG * (3f / 16f) * DITHER_STRENGTH;
                            bChannel[nextRowBase + x - 1] += errB * (3f / 16f) * DITHER_STRENGTH;
                        }

                        rChannel[nextRowBase + x] += errR * (5f / 16f) * DITHER_STRENGTH;
                        gChannel[nextRowBase + x] += errG * (5f / 16f) * DITHER_STRENGTH;
                        bChannel[nextRowBase + x] += errB * (5f / 16f) * DITHER_STRENGTH;

                        if (x + 1 < targetWidth)
                        {
                            rChannel[nextRowBase + x + 1] += errR * (1f / 16f) * DITHER_STRENGTH;
                            gChannel[nextRowBase + x + 1] += errG * (1f / 16f) * DITHER_STRENGTH;
                            bChannel[nextRowBase + x + 1] += errB * (1f / 16f) * DITHER_STRENGTH;
                        }
                    }

                    // Clamp to valid range
                    rChannel[i] = Math.Max(0f, Math.Min(1f, rChannel[i]));
                    gChannel[i] = Math.Max(0f, Math.Min(1f, gChannel[i]));
                    bChannel[i] = Math.Max(0f, Math.Min(1f, bChannel[i]));
                }
            }

            // Convert dithered RGB values to SE color characters
            StringBuilder result = RentStringBuilder((targetWidth + 1) * targetHeight);

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
        /// Converts a bitmap to SE color characters using 4×4 Bayer ordered dithering.
        /// Uses the standard threshold-comparison form: for each channel the fractional
        /// overshoot past its floor quantisation level is compared against the Bayer
        /// threshold.  All three channels share the same threshold at each pixel position
        /// so rounding decisions are correlated — this preserves the original hue and
        /// prevents the cyan/magenta chromatic noise that decorrelated per-channel
        /// thresholds produce on SE's coarse 3-bit-per-channel palette.
        /// Because the matrix is position-fixed, static regions produce an identical
        /// halftone pattern each frame — no temporal flickering.
        /// </summary>
        public static string ConvertToColorCharsOrdered(Bitmap image, int targetWidth, int targetHeight)
        {
            Bitmap resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, targetWidth, targetHeight);
            }

            BitmapData bmpData = resized.LockBits(
                new Rectangle(0, 0, targetWidth, targetHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int pixelBytes = stride * targetHeight;
            byte[] pixels = RentPixelBuf(pixelBytes);
            Marshal.Copy(bmpData.Scan0, pixels, 0, pixelBytes);
            resized.UnlockBits(bmpData);
            resized.Dispose();

            StringBuilder result = RentStringBuilder((targetWidth + 1) * targetHeight);

            for (int y = 0; y < targetHeight; y++)
            {
                int rowOffset = y * stride;
                int by = y & 3;

                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    // Format24bppRgb is BGR order.
                    //
                    // All three channels share the SAME Bayer threshold so the
                    // rounding decision is correlated across R/G/B.  This preserves
                    // the original pixel hue: a near-neutral gray dithers between
                    // two neutral grays rather than sprouting false cyan/magenta
                    // from independent per-channel rounding.
                    float thresh = _bayerNorm[by, x & 3] * BAYER_COLOR_SCALE + 0.5f;

                    float scaledR = pixels[idx + 2] / 255f * 7f;
                    float scaledG = pixels[idx + 1] / 255f * 7f;
                    float scaledB = pixels[idx    ] / 255f * 7f;

                    int rFloor = (int)scaledR;
                    int gFloor = (int)scaledG;
                    int bFloor = (int)scaledB;

                    int rInt = ((scaledR - rFloor) > thresh) ? rFloor + 1 : rFloor;
                    int gInt = ((scaledG - gFloor) > thresh) ? gFloor + 1 : gFloor;
                    int bInt = ((scaledB - bFloor) > thresh) ? bFloor + 1 : bFloor;

                    if (rInt > 7) rInt = 7;
                    if (gInt > 7) gInt = 7;
                    if (bInt > 7) bInt = 7;

                    result.Append((char)(SE_COLOR_BASE + ((rInt << 6) | (gInt << 3) | bInt)));
                }

                if (y < targetHeight - 1)
                    result.Append('\n');
            }

            return result.ToString();
        }

        /// <summary>
        /// Converts a bitmap to grayscale ASCII using fast LockBits pixel access
        /// with contrast enhancement.
        /// Pass forGrid=true when the output will be split into quadrants; this skips
        /// the 2:1 aspect-ratio correction so the row count equals targetHeight.
        /// </summary>
        public static string ConvertToAscii(Bitmap image, int targetWidth, int targetHeight, bool useBlockMode = true, bool forGrid = false)
        {
            char[] charRamp = WHIP_RAMP;

            // SE LCD characters are roughly 1:2 (width:height).
            // Both grid and single paths use /2 for consistent aspect correction.
            // If the bottom row clips on a single LCD, use the Content Shift slider.
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
            int byteCount = stride * adjustedHeight;
            byte[] pixels = RentPixelBuf(byteCount);
            Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);

            resized.UnlockBits(bmpData);

            // Convert to ASCII using static density-linearized LUT
            StringBuilder result = RentStringBuilder((targetWidth + 1) * adjustedHeight);

            for (int y = 0; y < adjustedHeight; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    // Format24bppRgb is BGR order — compute grayscale as int for LUT lookup
                    int gray = (int)(pixels[idx + 2] * 0.299f + pixels[idx + 1] * 0.587f + pixels[idx] * 0.114f);
                    if (gray < 0) gray = 0; else if (gray > 255) gray = 255;

                    result.Append(charRamp[_grayToCharIdx[gray]]);
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
                    // No contrast adjustment on colour values — see CONTRAST comment.
                    byte pb = pixels[idx];
                    byte pg = pixels[idx + 1];
                    byte pr = pixels[idx + 2];

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
        /// Converts with Floyd-Steinberg dithering using fast LockBits.
        /// Pass forGrid=true when the output will be split into quadrants; this skips
        /// the 2:1 aspect-ratio correction so the row count equals targetHeight.
        /// </summary>
        public static string ConvertToAsciiDithered(Bitmap image, int targetWidth, int targetHeight, bool forGrid = false)
        {
            // Both grid and single paths use /2 for consistent aspect correction.
            // If the bottom row clips on a single LCD, use the Content Shift slider.
            int adjustedHeight = targetHeight / 2;

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
            int pixelBytes = stride * adjustedHeight;
            byte[] pixels = RentPixelBuf(pixelBytes);
            Marshal.Copy(bmpData.Scan0, pixels, 0, pixelBytes);

            resized.UnlockBits(bmpData);
            resized.Dispose();

            // Reuse per-thread flat float[] buffer instead of allocating float[,] every frame.
            // Layout: row-major [y * targetWidth + x] — same pattern as the color dithered path.
            int total = targetWidth * adjustedHeight;
            if (_grayBuf == null || _grayBuf.Length < total)
                _grayBuf = new float[total];
            else
                Array.Clear(_grayBuf, 0, total);
            float[] grayscale = _grayBuf;

            // Convert to density-linearized grayscale via static LUT
            // (gamma + contrast + density linearisation folded into one lookup).
            for (int y = 0; y < adjustedHeight; y++)
            {
                int rowOffset = y * stride;
                int rowBase = y * targetWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    int idx = rowOffset + x * 3;
                    int gray = (int)(pixels[idx + 2] * 0.299f + pixels[idx + 1] * 0.587f + pixels[idx] * 0.114f);
                    if (gray > 255) gray = 255;
                    grayscale[rowBase + x] = _grayToLinear[gray];
                }
            }

            // Apply Floyd-Steinberg dithering
            for (int y = 0; y < adjustedHeight; y++)
            {
                int rowBase = y * targetWidth;
                int nextRowBase = rowBase + targetWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    int i = rowBase + x;
                    float oldPixel = grayscale[i];

                    // WHIP_RAMP with density-linearized input: dithering operates in
                    // perceptually uniform space so the dot chars (·˙°) are confined
                    // to a narrow brightness band, avoiding the tartan artefact they
                    // cause when FS alternates between them frequently.
                    int levels = WHIP_RAMP.Length;
                    float newPixel = (float)Math.Round(oldPixel * (levels - 1)) / (levels - 1);
                    grayscale[i] = newPixel;

                    float error = oldPixel - newPixel;

                    // Distribute error to neighboring pixels
                    if (x + 1 < targetWidth)
                        grayscale[i + 1] += error * 7f / 16f;

                    if (y + 1 < adjustedHeight)
                    {
                        if (x > 0)
                            grayscale[nextRowBase + x - 1] += error * 3f / 16f;

                        grayscale[nextRowBase + x] += error * 5f / 16f;

                        if (x + 1 < targetWidth)
                            grayscale[nextRowBase + x + 1] += error * 1f / 16f;
                    }

                    // Clamp values
                    grayscale[i] = Math.Max(0, Math.Min(1, grayscale[i]));
                }
            }

            // Convert to ASCII
            StringBuilder result = RentStringBuilder(targetWidth * adjustedHeight + adjustedHeight);

            for (int y = 0; y < adjustedHeight; y++)
            {
                int rowBase = y * targetWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    float brightness = grayscale[rowBase + x];
                    int charIndex = (int)(brightness * (WHIP_RAMP.Length - 1) + 0.5f);
                    charIndex = Math.Max(0, Math.Min(WHIP_RAMP.Length - 1, charIndex));

                    result.Append(WHIP_RAMP[charIndex]);
                }

                if (y < adjustedHeight - 1)
                    result.Append('\n');
            }

            return result.ToString();
        }

        /// <summary>
        /// Converts a bitmap to grayscale ASCII using 4×4 Bayer ordered dithering.
        /// Unlike Floyd-Steinberg (which diffuses quantisation error sequentially),
        /// Bayer dithering applies the same fixed threshold to the same pixel position
        /// on every frame.  Static regions of the video produce an identical halftone
        /// pattern each frame — no crawling or flickering between updates.
        /// Also faster than Floyd-Steinberg: no sequential dependency, no per-frame
        /// float[,] allocation.
        /// </summary>
        public static string ConvertToAsciiOrdered(Bitmap image, int targetWidth, int targetHeight, bool forGrid = false)
        {
            // SE LCD characters are roughly 1:2 (width:height).
            // Both grid and single paths use /2 for consistent aspect correction.
            // If the bottom row clips on a single LCD, use the Content Shift slider.
            int adjustedHeight = targetHeight / 2;

            Bitmap resized = new Bitmap(targetWidth, adjustedHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, targetWidth, adjustedHeight);
            }

            BitmapData bmpData = resized.LockBits(
                new Rectangle(0, 0, targetWidth, adjustedHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int pixelBytes = stride * adjustedHeight;
            byte[] pixels = RentPixelBuf(pixelBytes);
            Marshal.Copy(bmpData.Scan0, pixels, 0, pixelBytes);
            resized.UnlockBits(bmpData);
            resized.Dispose();

            int   rampLen   = WHIP_RAMP.Length; // 11
            float rampScale = rampLen - 1;        // 10.0f  (one quantisation step = 1/10 in density-linear space)

            StringBuilder result = RentStringBuilder((targetWidth + 1) * adjustedHeight);

            for (int y = 0; y < adjustedHeight; y++)
            {
                int rowOffset = y * stride;
                int by        = y & 3;

                for (int x = 0; x < targetWidth; x++)
                {
                    int idx  = rowOffset + x * 3;
                    int gray = (int)(pixels[idx + 2] * 0.299f + pixels[idx + 1] * 0.587f + pixels[idx] * 0.114f);
                    if (gray > 255) gray = 255;

                    // Apply Bayer threshold in density-linearized space: shift by
                    // ±(0.5/rampScale) so adjacent positions resolve to different levels.
                    float lum = _grayToLinear[gray] + _bayerNorm[by, x & 3] / rampScale;
                    lum = lum < 0f ? 0f : (lum > 1f ? 1f : lum);

                    int charIndex = (int)(lum * rampScale + 0.5f);
                    if (charIndex >= rampLen) charIndex = rampLen - 1;

                    result.Append(WHIP_RAMP[charIndex]);
                }

                if (y < adjustedHeight - 1)
                    result.Append('\n');
            }

            return result.ToString();
        }

        /// <summary>
        /// GZip-compresses ASCII art and encodes as base64 for compact transmission.
        /// Receiver must detect the GZ mode suffix and decompress accordingly.
        /// Reuses per-thread MemoryStream and UTF-8 byte buffer to avoid LOH allocations.
        /// </summary>
        public static string CompressAscii(string ascii)
        {
            // Reuse UTF-8 byte buffer — color chars (0xE100) are 3 bytes each in UTF-8
            int maxBytes = Encoding.UTF8.GetMaxByteCount(ascii.Length);
            if (_utf8Buf == null || _utf8Buf.Length < maxBytes)
                _utf8Buf = new byte[maxBytes];
            int byteCount = Encoding.UTF8.GetBytes(ascii, 0, ascii.Length, _utf8Buf, 0);

            // Reuse MemoryStream — SetLength(0) resets position without releasing buffer
            if (_compressMs == null)
                _compressMs = new MemoryStream(maxBytes);
            else
                _compressMs.SetLength(0);

            using (var gz = new GZipStream(_compressMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                gz.Write(_utf8Buf, 0, byteCount);
            }

            // Use GetBuffer() + length to avoid the ToArray() copy
            return Convert.ToBase64String(_compressMs.GetBuffer(), 0, (int)_compressMs.Length);
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
