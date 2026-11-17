using System;
using System.Collections.Generic;

namespace OpenSlideSharp;

/// <summary>
/// The slide members the rendering pipeline reads: the duck-typed slide that openslide-python's
/// <c>DeepZoomGenerator</c> accepts (an <c>OpenSlide</c> or an <c>ImageSlide</c>).
/// </summary>
internal interface IRenderSource
{
    /// <summary>Gets the size of each level, indexed by level number.</summary>
    IReadOnlyList<SlideSize> LevelDimensions { get; }

    /// <summary>Gets the downsample of each level relative to level 0, indexed by level number.</summary>
    IReadOnlyList<double> LevelDownsamples { get; }

    /// <summary>Gets the ICC profile of the pixels, or an empty value if there is none.</summary>
    ReadOnlyMemory<byte> ColorProfile { get; }

    /// <summary>Returns the level best suited to <paramref name="downsample"/>, like <c>openslide_get_best_level_for_downsample</c>.</summary>
    int GetBestLevelForDownsample(double downsample);

    /// <summary>Returns the property value, or <see langword="null"/> if the source has no such property.</summary>
    string? GetProperty(string name);

    /// <summary>
    /// Reads a region as premultiplied BGRA into the first <c>width * height * 4</c> bytes of
    /// <paramref name="destination"/>, writing every one of them; areas outside the source are transparent zeros.
    /// </summary>
    void ReadRegion(long x, long y, int level, int width, int height, Span<byte> destination);

    /// <summary>Describes the source for <c>ToString</c>, for example <c>OpenSlide(path)</c>.</summary>
    string Describe();
}
