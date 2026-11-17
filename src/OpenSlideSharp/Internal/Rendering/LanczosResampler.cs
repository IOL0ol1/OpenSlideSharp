using System;

namespace OpenSlideSharp;

/// <summary>Lanczos-3 resampler for 4-channel 8-bit images, ported from Pillow's libImaging/Resample.c so output matches byte for byte.</summary>
internal static class LanczosResampler
{
    private const int PrecisionBits = 22;
    private const int Channels = 4;

    /// <summary>Resizes interleaved 4-channel pixels from <paramref name="sourceWidth"/>x<paramref name="sourceHeight"/> to <paramref name="width"/>x<paramref name="height"/>.</summary>
    internal static void Resize(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, Span<byte> destination, int width, int height)
    {
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        long srcLen = (long)sourceWidth * sourceHeight * Channels;
        long dstLen = (long)width * height * Channels;
        if (source.Length < srcLen) throw new ArgumentException("Source buffer is too short.", nameof(source));
        if (destination.Length < dstLen) throw new ArgumentException("Destination buffer is too short.", nameof(destination));

        bool needH = width != sourceWidth;
        bool needV = height != sourceHeight;
        if (!needH && !needV)
        {
            source.Slice(0, (int)srcLen).CopyTo(destination);
            return;
        }

        int[] boundsH = Array.Empty<int>(), kernelH = Array.Empty<int>();
        int ksizeH = 0, ksizeV = 0;
        int[] boundsV = Array.Empty<int>(), kernelV = Array.Empty<int>();
        if (needH) ksizeH = PrecomputeCoeffs(sourceWidth, width, out boundsH, out kernelH);
        if (needV) ksizeV = PrecomputeCoeffs(sourceHeight, height, out boundsV, out kernelV);

        // Rows of the source that the vertical pass uses.
        int firstRow = 0, lastRow = sourceHeight;
        if (needV)
        {
            firstRow = boundsV[0];
            lastRow = boundsV[(height - 1) * 2] + boundsV[(height - 1) * 2 + 1];
            for (int i = 0; i < height; i++) boundsV[i * 2] -= firstRow;
        }

        if (!needV)
        {
            Horizontal(source, sourceWidth, destination, width, 0, sourceHeight, boundsH, kernelH, ksizeH);
            return;
        }

        int rows = lastRow - firstRow;
        if (!needH)
        {
            Vertical(source.Slice(firstRow * sourceWidth * Channels), width, destination, height, boundsV, kernelV, ksizeV);
            return;
        }

        byte[] temp = new byte[(long)rows * width * Channels];
        Horizontal(source, sourceWidth, temp, width, firstRow, lastRow, boundsH, kernelH, ksizeH);
        Vertical(temp, width, destination, height, boundsV, kernelV, ksizeV);
    }

    private static void Horizontal(ReadOnlySpan<byte> src, int srcWidth, Span<byte> dst, int dstWidth, int rowStart, int rowEnd, int[] bounds, int[] kernel, int ksize)
    {
        for (int row = rowStart; row < rowEnd; row++)
        {
            int srcRow = row * srcWidth * Channels;
            int dstRow = (row - rowStart) * dstWidth * Channels;
            for (int xx = 0; xx < dstWidth; xx++)
            {
                int xmin = bounds[xx * 2], count = bounds[xx * 2 + 1];
                int k0 = xx * ksize;
                for (int c = 0; c < Channels; c++)
                {
                    int ss = 1 << (PrecisionBits - 1);
                    int p = srcRow + xmin * Channels + c;
                    for (int x = 0; x < count; x++, p += Channels)
                        ss += src[p] * kernel[k0 + x];
                    dst[dstRow + xx * Channels + c] = Clip8(ss);
                }
            }
        }
    }

    private static void Vertical(ReadOnlySpan<byte> src, int width, Span<byte> dst, int dstHeight, int[] bounds, int[] kernel, int ksize)
    {
        int stride = width * Channels;
        for (int yy = 0; yy < dstHeight; yy++)
        {
            int ymin = bounds[yy * 2], count = bounds[yy * 2 + 1];
            int k0 = yy * ksize;
            int dstRow = yy * stride;
            for (int i = 0; i < stride; i++)
            {
                int ss = 1 << (PrecisionBits - 1);
                int p = ymin * stride + i;
                for (int y = 0; y < count; y++, p += stride)
                    ss += src[p] * kernel[k0 + y];
                dst[dstRow + i] = Clip8(ss);
            }
        }
    }

    private static byte Clip8(int ss)
    {
        if (ss >= (1 << 30)) return 255;
        if (ss <= 0) return 0;
        return (byte)(ss >> PrecisionBits);
    }

    private static double Sinc(double x)
    {
        if (x == 0.0) return 1.0;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    private static double Lanczos(double x) =>
        x >= -3.0 && x < 3.0 ? Sinc(x) * Sinc(x / 3.0) : 0.0;

    /// <summary>Pillow precompute_coeffs plus normalize_coeffs_8bpc; returns ksize. bounds holds (min, count) pairs.</summary>
    private static int PrecomputeCoeffs(int inSize, int outSize, out int[] bounds, out int[] kernel)
    {
        double scale = (double)inSize / outSize;
        double filterscale = scale < 1.0 ? 1.0 : scale;
        double support = 3.0 * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;

        bounds = new int[outSize * 2];
        kernel = new int[outSize * ksize];
        double[] k = new double[ksize];
        double ss = 1.0 / filterscale;

        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;

            double ww = 0.0;
            for (int x = 0; x < xmax; x++)
            {
                double w = Lanczos((x + xmin - center + 0.5) * ss);
                k[x] = w;
                ww += w;
            }

            for (int x = 0; x < xmax; x++)
            {
                if (ww != 0.0) k[x] /= ww;
                double v = k[x];
                kernel[xx * ksize + x] = v < 0
                    ? (int)(-0.5 + v * (1 << PrecisionBits))
                    : (int)(0.5 + v * (1 << PrecisionBits));
            }

            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        return ksize;
    }
}
