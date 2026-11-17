using System;
using System.Collections.Generic;
using System.Text;

namespace OpenSlideSharp;

/// <summary>
/// Generates Deep Zoom tiles and metadata for a slide or an associated image, matching openslide-python's
/// <c>openslide.deepzoom.DeepZoomGenerator</c>: the same levels, tile grids, <c>read_region</c> arguments and DZI
/// text. Level 0 is the smallest (1 x 1) level and <see cref="LevelCount"/> - 1 the full-resolution one.
/// </summary>
/// <remarks>
/// Everything is read from the source in the constructor. Tiles are always exactly the size
/// <see cref="GetTileDimensions"/> reports (Pillow's <c>thumbnail</c> can differ by one pixel; this is
/// intentional). Instances are immutable and <see cref="GetTile"/> may be called from several threads at once.
/// </remarks>
public sealed class DeepZoomGenerator
{
    private readonly IRenderSource _source;
    private readonly Action? _ensureUsable;
    private readonly SlideGeometry _geometry;
    private readonly RegionRenderer _renderer;
    private readonly TileGridSize[] _levelTiles;
    private readonly SlideSize[] _levelDimensions;
    private readonly ReadOnlyMemory<byte> _iccProfile;

    /// <summary>Creates a generator for a slide.</summary>
    /// <param name="slide">The slide.</param>
    /// <param name="tileSize">Width and height of a tile; <c>tileSize + 2 * overlap</c> should be a power of two for best viewer performance.</param>
    /// <param name="overlap">Extra pixels added to each interior edge of a tile.</param>
    /// <param name="limitBounds"><see langword="true"/> to render only the non-empty slide region.</param>
    /// <param name="colorTransform">Converts tiles to sRGB; if given, tiles carry no ICC profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="slide"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileSize"/> is less than 1, or <paramref name="overlap"/> is negative or too large.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    public DeepZoomGenerator(OpenSlide slide, int tileSize = 254, int overlap = 1, bool limitBounds = false, IColorTransform? colorTransform = null)
        : this(CreateSource(slide), tileSize, overlap, limitBounds, colorTransform, slide.ThrowIfUnusable)
    {
    }

    /// <summary>Creates a generator for an associated image, which has one level and no properties.</summary>
    /// <param name="image">The associated image.</param>
    /// <param name="tileSize">Width and height of a tile.</param>
    /// <param name="overlap">Extra pixels added to each interior edge of a tile.</param>
    /// <param name="colorTransform">Converts tiles to sRGB; if given, tiles carry no ICC profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileSize"/> is less than 1, or <paramref name="overlap"/> is negative or too large.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    public DeepZoomGenerator(AssociatedImage image, int tileSize = 254, int overlap = 1, IColorTransform? colorTransform = null)
        : this(CreateSource(image), tileSize, overlap, false, colorTransform, image.Slide.ThrowIfUnusable)
    {
    }

    internal DeepZoomGenerator(IRenderSource source, int tileSize, int overlap, bool limitBounds, IColorTransform? colorTransform)
        : this(source, tileSize, overlap, limitBounds, colorTransform, null)
    {
    }

    private DeepZoomGenerator(
        IRenderSource source, int tileSize, int overlap, bool limitBounds, IColorTransform? colorTransform, Action? ensureUsable)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (tileSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "The tile size must be at least 1.");
        }

        if (overlap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), overlap, "The overlap must not be negative.");
        }

        if ((long)tileSize + (2L * overlap) > int.MaxValue / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), overlap, "The tile size plus twice the overlap is too large.");
        }

        _source = source;
        _ensureUsable = ensureUsable;
        TileSize = tileSize;
        Overlap = overlap;
        LimitBounds = limitBounds;

        _geometry = new SlideGeometry(source, limitBounds);
        _renderer = new RegionRenderer(source, _geometry, colorTransform);
        _iccProfile = colorTransform is null ? source.ColorProfile : default;

        LevelCount = _geometry.LevelCount;
        _levelDimensions = new SlideSize[LevelCount];
        _levelTiles = new TileGridSize[LevelCount];
        long total = 0;
        for (int level = 0; level < LevelCount; level++)
        {
            SlideSize size = _geometry.GetLevelSize(LevelCount - 1 - level);
            _levelDimensions[level] = size;
            _levelTiles[level] = new TileGridSize(CeilDiv(size.Width, tileSize), CeilDiv(size.Height, tileSize));
            total += _levelTiles[level].Columns * _levelTiles[level].Rows;
        }

        TileCount = total;
    }

    /// <summary>Gets the width and height of a tile, not counting overlap.</summary>
    public int TileSize { get; }

    /// <summary>Gets the number of extra pixels added to each interior edge of a tile.</summary>
    public int Overlap { get; }

    /// <summary>Gets a value indicating whether only the non-empty slide region is rendered.</summary>
    public bool LimitBounds { get; }

    /// <summary>Gets the number of Deep Zoom levels.</summary>
    public int LevelCount { get; }

    /// <summary>Gets the tile grid size of each level, smallest level first.</summary>
    public IReadOnlyList<TileGridSize> LevelTiles => _levelTiles;

    /// <summary>Gets the pixel size of each level, smallest level first.</summary>
    public IReadOnlyList<SlideSize> LevelDimensions => _levelDimensions;

    /// <summary>Gets the total number of tiles in all levels.</summary>
    public long TileCount { get; }

    /// <summary>Renders a tile as opaque BGRA, with the size <see cref="GetTileDimensions"/> reports.</summary>
    /// <remarks>The result carries the source's ICC profile, or none if a color transform was given.</remarks>
    /// <param name="level">The Deep Zoom level.</param>
    /// <param name="column">The tile column within the level.</param>
    /// <param name="row">The tile row within the level.</param>
    /// <returns>The tile.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level, column or row is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">Reading failed.</exception>
    public PixelImage GetTile(int level, long column, long row)
    {
        _ensureUsable?.Invoke();
        TileInfo info = GetTileInfo(level, column, row);
        var data = new byte[OpenSlide.CheckedPixelBytes(info.Width, info.Height, "tile")];
        _renderer.Render(info.Exponent, info.X, info.Y, info.Width, info.Height, data);
        return new PixelImage(info.Width, info.Height, PixelFormat.Bgra32Opaque, data, _iccProfile);
    }

    /// <summary>Returns the <c>OpenSlide.ReadRegion</c> arguments for a tile.</summary>
    /// <param name="level">The Deep Zoom level.</param>
    /// <param name="column">The tile column within the level.</param>
    /// <param name="row">The tile row within the level.</param>
    /// <returns>The region to read.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level, column or row is out of range.</exception>
    public TileRegion GetTileCoordinates(int level, long column, long row) => GetTileInfo(level, column, row).Region;

    /// <summary>Returns the size in pixels of a tile, including its overlap.</summary>
    /// <param name="level">The Deep Zoom level.</param>
    /// <param name="column">The tile column within the level.</param>
    /// <param name="row">The tile row within the level.</param>
    /// <returns>The tile size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level, column or row is out of range.</exception>
    public SlideSize GetTileDimensions(int level, long column, long row)
    {
        TileInfo info = GetTileInfo(level, column, row);
        return new SlideSize(info.Width, info.Height);
    }

    /// <summary>Returns the XML text of the <c>.dzi</c> file, identical to openslide-python's <c>get_dzi</c>.</summary>
    /// <param name="format">The tile format, such as <c>"png"</c> or <c>"jpeg"</c>.</param>
    /// <returns>The XML, without an XML declaration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    public string GetDzi(string format)
    {
        if (format is null)
        {
            throw new ArgumentNullException(nameof(format));
        }

        SlideSize size = _geometry.ActiveSize;
        var sb = new StringBuilder();
        sb.Append("<Image TileSize=\"").Append(TileSize).Append("\" Overlap=\"").Append(Overlap).Append("\" Format=\"");
        foreach (char c in format)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\r': sb.Append("&#13;"); break;
                case '\n': sb.Append("&#10;"); break;
                case '\t': sb.Append("&#09;"); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append("\" xmlns=\"http://schemas.microsoft.com/deepzoom/2008\"><Size Width=\"")
            .Append(size.Width).Append("\" Height=\"").Append(size.Height).Append("\" /></Image>");
        return sb.ToString();
    }

    /// <summary>Describes the generator and its parameters.</summary>
    /// <returns>For example <c>DeepZoomGenerator(OpenSlide(path), tileSize: 254, overlap: 1, limitBounds: False)</c>.</returns>
    public override string ToString() =>
        $"DeepZoomGenerator({_source.Describe()}, tileSize: {TileSize}, overlap: {Overlap}, limitBounds: {(LimitBounds ? "True" : "False")})";

    // Port of _get_tile_info: validates the address, then computes the overlap, final tile size and read region.
    private TileInfo GetTileInfo(int level, long column, long row)
    {
        if (level < 0 || level >= LevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Invalid level.");
        }

        TileGridSize grid = _levelTiles[level];
        if (column < 0 || column >= grid.Columns)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, "Invalid address.");
        }

        if (row < 0 || row >= grid.Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, "Invalid address.");
        }

        SlideSize levelSize = _levelDimensions[level];
        long overlapLeft = column != 0 ? Overlap : 0;
        long overlapTop = row != 0 ? Overlap : 0;
        long overlapRight = column != grid.Columns - 1 ? Overlap : 0;
        long overlapBottom = row != grid.Rows - 1 ? Overlap : 0;

        long width = Math.Min(TileSize, levelSize.Width - ((long)TileSize * column)) + overlapLeft + overlapRight;
        long height = Math.Min(TileSize, levelSize.Height - ((long)TileSize * row)) + overlapTop + overlapBottom;

        int exponent = LevelCount - 1 - level;
        long x = ((long)TileSize * column) - overlapLeft;
        long y = ((long)TileSize * row) - overlapTop;
        SourceRegion region = _geometry.Map(exponent, x, y, width, height);
        return new TileInfo(
            new TileRegion(region.X, region.Y, region.Level, region.Width, region.Height), (int)width, (int)height, exponent, x, y);
    }

    private static IRenderSource CreateSource(OpenSlide slide)
    {
        if (slide is null)
        {
            throw new ArgumentNullException(nameof(slide));
        }

        slide.ThrowIfUnusable();
        return new SlideRenderSource(slide);
    }

    private static IRenderSource CreateSource(AssociatedImage image)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        image.Slide.ThrowIfUnusable();
        return new AssociatedImageRenderSource(image);
    }

    private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;

    private readonly record struct TileInfo(TileRegion Region, int Width, int Height, int Exponent, long X, long Y);
}
