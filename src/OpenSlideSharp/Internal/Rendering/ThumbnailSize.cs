using System;

namespace OpenSlideSharp;

/// <summary>The output size of Pillow's <c>Image.thumbnail</c>, ported from its <c>preserve_aspect_ratio</c> (Pillow 13.0).</summary>
internal static class ThumbnailSize
{
    /// <summary>
    /// Returns the size a <paramref name="width"/> x <paramref name="height"/> image is scaled to so that it fits in
    /// <paramref name="maxWidth"/> x <paramref name="maxHeight"/> with its aspect ratio kept, or
    /// <see langword="null"/> if it is not scaled: it already fits, or it is empty (where Pillow would divide by zero).
    /// </summary>
    internal static (long W, long H)? Compute(long width, long height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        long x = maxWidth;
        long y = maxHeight;
        if (x >= width && y >= height)
        {
            return null;
        }

        double aspect = (double)width / height;
        if ((double)x / y >= aspect)
        {
            x = RoundAspect(y * aspect, n => Math.Abs(aspect - ((double)n / y)));
        }
        else
        {
            y = RoundAspect(x / aspect, n => n == 0 ? 0 : Math.Abs(aspect - ((double)x / n)));
        }

        return (x, y);
    }

    // Python: max(min(math.floor(number), math.ceil(number), key=key), 1). min keeps the first of equal keys, so
    // a tie picks the floor.
    private static long RoundAspect(double number, Func<long, double> key)
    {
        long floor = (long)Math.Floor(number);
        long ceiling = (long)Math.Ceiling(number);
        long nearest = key(ceiling) < key(floor) ? ceiling : floor;
        return Math.Max(nearest, 1);
    }
}
