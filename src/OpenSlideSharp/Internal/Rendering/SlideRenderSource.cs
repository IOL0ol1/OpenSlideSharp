using System;
using System.Collections.Generic;

namespace OpenSlideSharp;

/// <summary>Renders from an <see cref="OpenSlide"/>; every member forwards to the slide.</summary>
internal sealed class SlideRenderSource : IRenderSource
{
    private readonly OpenSlide _slide;

    internal SlideRenderSource(OpenSlide slide) => _slide = slide ?? throw new ArgumentNullException(nameof(slide));

    public IReadOnlyList<SlideSize> LevelDimensions => _slide.LevelDimensions;

    public IReadOnlyList<double> LevelDownsamples => _slide.LevelDownsamples;

    public ReadOnlyMemory<byte> ColorProfile => _slide.ColorProfile;

    public int GetBestLevelForDownsample(double downsample) => _slide.GetBestLevelForDownsample(downsample);

    public string? GetProperty(string name) => _slide.Properties.TryGetValue(name, out string? value) ? value : null;

    public void ReadRegion(long x, long y, int level, int width, int height, Span<byte> destination) =>
        _slide.ReadRegion(x, y, level, width, height, destination);

    public string Describe() => _slide.ToString();
}
