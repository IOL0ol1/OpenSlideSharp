using System;
using System.Collections.Generic;

namespace OpenSlideSharp.Viewer.Core;

/// <summary>
/// Splits each level of the slide into <see cref="TileSize"/> x <see cref="TileSize"/> tiles, in that level's
/// pixels; the tiles at the right and bottom edges are smaller.
/// </summary>
internal sealed class TileGrid
{
    public const int TileSize = 256;

    private readonly SlideSize[] _dimensions;
    private readonly double[] _downsamples;

    public TileGrid(IReadOnlyList<SlideSize> levelDimensions, IReadOnlyList<double> levelDownsamples)
    {
        ArgumentNullException.ThrowIfNull(levelDimensions);
        ArgumentNullException.ThrowIfNull(levelDownsamples);
        if (levelDimensions.Count != levelDownsamples.Count)
        {
            throw new ArgumentException(
                $"{levelDimensions.Count} level dimensions but {levelDownsamples.Count} downsamples.", nameof(levelDownsamples));
        }

        _dimensions = new SlideSize[levelDimensions.Count];
        _downsamples = new double[levelDownsamples.Count];
        for (int level = 0; level < _dimensions.Length; level++)
        {
            _dimensions[level] = levelDimensions[level];
            _downsamples[level] = levelDownsamples[level];
        }
    }

    /// <summary>
    /// Returns the tiles of <paramref name="level"/> that overlap the level-0 rectangle, row by row. The right and
    /// bottom edges are exclusive. A level that does not exist, or a rectangle that misses the slide, has none.
    /// </summary>
    public IEnumerable<TileKey> VisibleTiles(int level, double left0, double top0, double right0, double bottom0)
    {
        if (level < 0 || level >= _dimensions.Length)
        {
            yield break;
        }

        double downsample = _downsamples[level];
        SlideSize size = _dimensions[level];
        double left = Math.Max(left0 / downsample, 0);
        double top = Math.Max(top0 / downsample, 0);
        double right = Math.Min(right0 / downsample, size.Width);
        double bottom = Math.Min(bottom0 / downsample, size.Height);
        // Also false for NaN.
        if (!(right > left && bottom > top))
        {
            yield break;
        }

        long firstColumn = (long)Math.Floor(left / TileSize);
        long lastColumn = (long)Math.Ceiling(right / TileSize) - 1;
        long firstRow = (long)Math.Floor(top / TileSize);
        long lastRow = (long)Math.Ceiling(bottom / TileSize) - 1;
        for (long row = firstRow; row <= lastRow; row++)
        {
            for (long column = firstColumn; column <= lastColumn; column++)
            {
                yield return new TileKey(level, column, row);
            }
        }
    }

    /// <summary>
    /// Returns the arguments of <see cref="OpenSlide.ReadRegion(long, long, int, int, int, Span{byte})"/> for the
    /// tile: its top-left corner in level-0 coordinates and its size in pixels of its level.
    /// </summary>
    public (long X0, long Y0, int Width, int Height) Region(TileKey key)
    {
        (SlideSize size, double downsample) = Level(key);
        long x = key.Column * TileSize;
        long y = key.Row * TileSize;
        return (
            (long)Math.Floor(x * downsample),
            (long)Math.Floor(y * downsample),
            (int)Math.Min(TileSize, size.Width - x),
            (int)Math.Min(TileSize, size.Height - y));
    }

    /// <summary>
    /// Returns the level-0 rectangle the tile covers, where it is drawn. Neighbouring tiles share their edges
    /// exactly, so there are no seams between them.
    /// </summary>
    public (double Left0, double Top0, double Right0, double Bottom0) Bounds(TileKey key)
    {
        (long _, long _, int width, int height) = Region(key);
        double downsample = _downsamples[key.Level];
        double x = key.Column * TileSize;
        double y = key.Row * TileSize;
        return (x * downsample, y * downsample, (x + width) * downsample, (y + height) * downsample);
    }

    private (SlideSize Size, double Downsample) Level(TileKey key)
    {
        if (key.Level < 0 || key.Level >= _dimensions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "The level does not exist.");
        }

        SlideSize size = _dimensions[key.Level];
        if (key.Column < 0 || key.Row < 0 || key.Column * TileSize >= size.Width || key.Row * TileSize >= size.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "The tile is outside its level.");
        }

        return (size, _downsamples[key.Level]);
    }
}
