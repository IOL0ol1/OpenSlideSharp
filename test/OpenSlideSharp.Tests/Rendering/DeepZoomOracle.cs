using System;
using System.Collections.Generic;

namespace OpenSlideSharp.Tests.Rendering;

/// <summary>The geometry part of Oracles/deepzoom.json (see generate_oracles.py).</summary>
public sealed class DeepZoomOracle
{
    private static readonly Lazy<DeepZoomOracle> Cached = new(() => OracleData.Load<DeepZoomOracle>("deepzoom.json"));

    public List<GeometryOracle> Geometry { get; set; } = new();

    public List<TileOracle> Tiles { get; set; } = new();

    /// <summary>Parses deepzoom.json once per test run; callers must not modify the result.</summary>
    public static DeepZoomOracle Load() => Cached.Value;
}

public sealed class GeometryOracle
{
    public string Source { get; set; } = "";

    /// <summary><c>"slide"</c> for a fixture file, <c>"fake"</c> for a synthetic slide described by <see cref="Fake"/>.</summary>
    public string Kind { get; set; } = "";

    public int TileSize { get; set; }

    public int Overlap { get; set; }

    public bool LimitBounds { get; set; }

    public FakeSlideOracle? Fake { get; set; }

    public int LevelCount { get; set; }

    public long TileCount { get; set; }

    public string DziJpeg { get; set; } = "";

    public string DziPng { get; set; } = "";

    /// <summary>Deep Zoom levels, lowest resolution first (index = Deep Zoom level).</summary>
    public List<LevelOracle> Levels { get; set; } = new();
}

public sealed class FakeSlideOracle
{
    public long[][] LevelDimensions { get; set; } = Array.Empty<long[]>();

    public double[] LevelDownsamples { get; set; } = Array.Empty<double>();

    public Dictionary<string, string> Properties { get; set; } = new();
}

public sealed class LevelOracle
{
    public long[] Dimensions { get; set; } = Array.Empty<long>();

    public long[] Tiles { get; set; } = Array.Empty<long>();

    /// <summary>Each entry is <c>[col, row, x, y, slideLevel, w, h, tileW, tileH]</c>.</summary>
    public long[][] TileInfo { get; set; } = Array.Empty<long[]>();
}

public sealed class TileOracle
{
    /// <summary>A fixture file name, or <c>file#name</c> for an associated image of that file.</summary>
    public string Source { get; set; } = "";

    public string Kind { get; set; } = "";

    public int TileSize { get; set; }

    public int Overlap { get; set; }

    public int Level { get; set; }

    public long Col { get; set; }

    public long Row { get; set; }

    public int[] ExpectedSize { get; set; } = Array.Empty<int>();

    public int[] PythonSize { get; set; } = Array.Empty<int>();

    public bool Exact { get; set; }

    public int IccLength { get; set; }

    public string File { get; set; } = "";
}
