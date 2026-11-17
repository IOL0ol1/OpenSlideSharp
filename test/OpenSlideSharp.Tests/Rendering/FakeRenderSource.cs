using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OpenSlideSharp.Tests.Rendering;

/// <summary>
/// An in-memory render source with the slide members DeepZoom geometry uses, mirroring generate_oracles.py's
/// <c>FakeSlide</c>. Reads return transparent pixels and are counted.
/// </summary>
internal sealed class FakeRenderSource : IRenderSource
{
    private readonly SlideSize[] _levelDimensions;
    private readonly double[] _levelDownsamples;
    private readonly Dictionary<string, string> _properties;
    private readonly string _name;
    private int _readCount;

    public FakeRenderSource(
        string name,
        IEnumerable<SlideSize> levelDimensions,
        IEnumerable<double> levelDownsamples,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        _name = name;
        _levelDimensions = levelDimensions.ToArray();
        _levelDownsamples = levelDownsamples.ToArray();
        _properties = properties?.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, string>();
        if (_levelDimensions.Length == 0 || _levelDimensions.Length != _levelDownsamples.Length)
            throw new ArgumentException("Need the same non-zero number of level dimensions and downsamples.");
    }

    /// <summary>A one-level slide of the given size with the given properties.</summary>
    public static FakeRenderSource SingleLevel(long width, long height, params (string Name, string Value)[] properties) =>
        new("single", new[] { new SlideSize(width, height) }, new[] { 1.0 },
            properties.ToDictionary(p => p.Name, p => p.Value));

    /// <summary>The synthetic slide a <c>kind == "fake"</c> geometry oracle entry was generated from.</summary>
    public static FakeRenderSource FromOracle(GeometryOracle entry)
    {
        FakeSlideOracle fake = entry.Fake ?? throw new ArgumentException(entry.Source + " is not a fake slide entry.");
        return new FakeRenderSource(
            entry.Source,
            fake.LevelDimensions.Select(d => new SlideSize(d[0], d[1])),
            fake.LevelDownsamples,
            fake.Properties);
    }

    /// <summary>Number of <see cref="ReadRegion"/> calls so far.</summary>
    public int ReadCount => Volatile.Read(ref _readCount);

    public IReadOnlyList<SlideSize> LevelDimensions => _levelDimensions;

    public IReadOnlyList<double> LevelDownsamples => _levelDownsamples;

    public ReadOnlyMemory<byte> ColorProfile => default;

    // Same algorithm as openslide_get_best_level_for_downsample() and FakeSlide.
    public int GetBestLevelForDownsample(double downsample)
    {
        if (downsample < _levelDownsamples[0])
            return 0;
        for (int i = 1; i < _levelDownsamples.Length; i++)
            if (downsample < _levelDownsamples[i])
                return i - 1;
        return _levelDownsamples.Length - 1;
    }

    public string? GetProperty(string name) => _properties.TryGetValue(name, out string? value) ? value : null;

    public void ReadRegion(long x, long y, int level, int width, int height, Span<byte> destination)
    {
        Interlocked.Increment(ref _readCount);
        destination.Slice(0, checked(width * height * 4)).Clear();
    }

    public string Describe() => $"FakeRenderSource({_name})";
}
