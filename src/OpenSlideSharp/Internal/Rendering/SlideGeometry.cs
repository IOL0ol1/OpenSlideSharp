using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenSlideSharp;

/// <summary>The <c>read_region</c> arguments for a rectangle: level 0 origin, slide level and size at that level.</summary>
internal readonly record struct SourceRegion(long X, long Y, int Level, long Width, long Height);

/// <summary>
/// Maps rectangles on power-of-two downsampled views of a slide to <c>read_region</c> arguments. A line-by-line
/// port of openslide-python's <c>DeepZoomGenerator.__init__</c> and <c>_get_tile_info</c> (deepzoom.py 1.4.7)
/// without the tile grid, keeping the same double arithmetic and operation order so results match exactly.
/// Everything is read from the source once, in the constructor; instances are immutable and thread-safe.
/// </summary>
/// <remarks>
/// A view is identified by its scale exponent <c>e</c>: it is the slide (or its active area) downsampled by
/// <c>2^e</c>, so <c>e = 0</c> is full resolution and <c>e = LevelCount - 1</c> is 1 x 1. Deep Zoom level
/// <c>k</c> is <c>e = LevelCount - 1 - k</c>.
/// <para>
/// Malformed properties never throw; they fall back to the defaults. With <c>limitBounds</c>,
/// <c>openslide.bounds-x/y/width/height</c> are parsed as decimal integers, and a value that is missing or not
/// an integer counts as missing: the offset is then 0 and the size is the level 0 size. A negative width or
/// height also counts as missing. Zero is kept, as in Python, and gives an empty active area. Negative x and y
/// are kept as well. The background is white unless <c>openslide.background-color</c> is 6 hex digits. The mpp
/// values are null unless they parse as finite, positive invariant-culture numbers.
/// </para>
/// </remarks>
internal sealed class SlideGeometry
{
    private readonly SlideSize[] _levelDimensions; // Python _l_dimensions (scaled to the active area if limitBounds)
    private readonly double[] _levelDownsamples;   // Python _l0_l_downsamples
    private readonly SlideSize[] _viewDimensions;  // Python _z_dimensions, indexed by scale exponent
    private readonly int[] _slideLevels;           // Python _slide_from_dz_level, indexed by scale exponent
    private readonly double[] _viewToLevel;        // Python _l_z_downsamples, indexed by scale exponent

    internal SlideGeometry(IRenderSource source, bool limitBounds)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        IReadOnlyList<SlideSize> sourceDimensions = source.LevelDimensions;
        _levelDownsamples = ToArray(source.LevelDownsamples);
        if (sourceDimensions.Count == 0 || sourceDimensions.Count != _levelDownsamples.Length)
        {
            throw new ArgumentException("The source must have the same non-zero number of level sizes and downsamples.", nameof(source));
        }

        // Slide level and offset
        _levelDimensions = ToArray(sourceDimensions);
        if (limitBounds)
        {
            // Level 0 coordinate offset
            OffsetX = ParseInteger(source.GetProperty(OpenSlideProperties.BoundsX)) ?? 0;
            OffsetY = ParseInteger(source.GetProperty(OpenSlideProperties.BoundsY)) ?? 0;

            // Slide level dimensions scale factor in each axis
            SlideSize l0 = sourceDimensions[0];
            double scaleX = SizeScale(ParseInteger(source.GetProperty(OpenSlideProperties.BoundsWidth)), l0.Width);
            double scaleY = SizeScale(ParseInteger(source.GetProperty(OpenSlideProperties.BoundsHeight)), l0.Height);

            // Dimensions of active area
            for (int level = 0; level < _levelDimensions.Length; level++)
            {
                SlideSize size = sourceDimensions[level];
                _levelDimensions[level] = new SlideSize(
                    (long)Math.Ceiling(size.Width * scaleX), (long)Math.Ceiling(size.Height * scaleY));
            }
        }

        ActiveSize = _levelDimensions[0];

        // Deep Zoom level dimensions, full resolution first: halve, rounding up, until 1 x 1.
        var views = new List<SlideSize> { ActiveSize };
        SlideSize view = ActiveSize;
        while (view.Width > 1 || view.Height > 1)
        {
            view = new SlideSize(Math.Max(1, (view.Width + 1) / 2), Math.Max(1, (view.Height + 1) / 2));
            views.Add(view);
        }

        _viewDimensions = views.ToArray();
        LevelCount = _viewDimensions.Length;

        // Preferred slide level and piecewise downsample for each view
        _slideLevels = new int[LevelCount];
        _viewToLevel = new double[LevelCount];
        double downsample = 1.0; // 2^e, exact
        for (int e = 0; e < LevelCount; e++, downsample *= 2)
        {
            int slideLevel = source.GetBestLevelForDownsample(downsample);
            if ((uint)slideLevel >= (uint)_levelDownsamples.Length)
            {
                throw new InvalidOperationException($"The source returned level {slideLevel} as best for downsample {downsample}.");
            }

            _slideLevels[e] = slideLevel;
            _viewToLevel[e] = downsample / _levelDownsamples[slideLevel];
        }

        // Slide background color, white if missing or malformed
        Compositor.TryParseBackground(
            source.GetProperty(OpenSlideProperties.BackgroundColor), out byte red, out byte green, out byte blue);
        Background = (red, green, blue);

        MppX = ParseMpp(source.GetProperty(OpenSlideProperties.MppX));
        MppY = ParseMpp(source.GetProperty(OpenSlideProperties.MppY));
    }

    /// <summary>Gets the size of the rendered area at full resolution (Python <c>_l0_dimensions</c>).</summary>
    internal SlideSize ActiveSize { get; }

    /// <summary>Gets the level 0 X of the rendered area's origin (Python <c>_l0_offset</c>).</summary>
    internal long OffsetX { get; }

    /// <summary>Gets the level 0 Y of the rendered area's origin (Python <c>_l0_offset</c>).</summary>
    internal long OffsetY { get; }

    /// <summary>Gets the number of views, from full resolution down to 1 x 1 (Python <c>_dz_levels</c>).</summary>
    internal int LevelCount { get; }

    /// <summary>Gets the slide background color (<c>openslide.background-color</c>), white if missing or malformed.</summary>
    internal (byte Red, byte Green, byte Blue) Background { get; }

    /// <summary>Gets <c>openslide.mpp-x</c>, or <see langword="null"/> if missing, malformed, not finite or not positive.</summary>
    internal double? MppX { get; }

    /// <summary>Gets <c>openslide.mpp-y</c>, or <see langword="null"/> if missing, malformed, not finite or not positive.</summary>
    internal double? MppY { get; }

    /// <summary>Returns the size of the view downsampled by <c>2^scaleExponent</c>.</summary>
    internal SlideSize GetLevelSize(int scaleExponent)
    {
        CheckScaleExponent(scaleExponent);
        return _viewDimensions[scaleExponent];
    }

    /// <summary>
    /// Returns the <c>read_region</c> arguments for the rectangle (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="width"/>, <paramref name="height"/>) of the view downsampled by <c>2^scaleExponent</c>:
    /// the origin is rounded down and the size up, and the size is clipped to the active area, so it can be
    /// zero or negative for a rectangle outside it.
    /// </summary>
    internal SourceRegion Map(int scaleExponent, long x, long y, long width, long height)
    {
        CheckScaleExponent(scaleExponent);

        // Get preferred slide level
        int slideLevel = _slideLevels[scaleExponent];
        double viewToLevel = _viewToLevel[scaleExponent];
        double levelDownsample = _levelDownsamples[slideLevel];
        SlideSize levelLimit = _levelDimensions[slideLevel];

        // Obtain the region coordinates
        double levelX = viewToLevel * x;
        double levelY = viewToLevel * y;

        // Round location down and size up, and add offset of active area
        long l0X = (long)((levelDownsample * levelX) + OffsetX);
        long l0Y = (long)((levelDownsample * levelY) + OffsetY);
        long levelWidth = Math.Min((long)Math.Ceiling(viewToLevel * width), levelLimit.Width - (long)Math.Ceiling(levelX));
        long levelHeight = Math.Min((long)Math.Ceiling(viewToLevel * height), levelLimit.Height - (long)Math.Ceiling(levelY));
        return new SourceRegion(l0X, l0Y, slideLevel, levelWidth, levelHeight);
    }

    private void CheckScaleExponent(int scaleExponent)
    {
        if ((uint)scaleExponent >= (uint)LevelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scaleExponent), scaleExponent, $"The scale exponent must be from 0 to {LevelCount - 1}.");
        }
    }

    // Python: int(properties.get(prop, l0_lim)) / l0_lim. A negative bounds width or height counts as missing
    // (Python would produce negative level sizes), while zero is kept as parsed, as in Python. An empty level 0
    // (where Python divides by zero) keeps the empty level sizes as they are.
    private static double SizeScale(long? boundsSize, long l0Size)
    {
        if (l0Size == 0)
        {
            return 1.0;
        }

        long size = boundsSize is long parsed && parsed >= 0 ? parsed : l0Size;
        return (double)size / l0Size;
    }

    private static long? ParseInteger(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : null;

    private static double? ParseMpp(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ||
            double.IsNaN(result) || double.IsInfinity(result) || result <= 0)
        {
            return null;
        }

        return result;
    }

    private static T[] ToArray<T>(IReadOnlyList<T> list)
    {
        var array = new T[list.Count];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = list[i];
        }

        return array;
    }
}
