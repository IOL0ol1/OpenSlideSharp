using OpenSlideSharp.SkiaSharp;

namespace OpenSlideSharp.AspNetCore;

/// <summary>
/// Settings for the OpenSlide endpoints and the <see cref="SlideCache"/> they share. Register them with
/// <c>AddOpenSlide</c> (<see cref="OpenSlideServiceCollectionExtensions"/>), which validates them the first time
/// they are resolved.
/// </summary>
public sealed class OpenSlideServerOptions
{
    /// <summary>
    /// Gets or sets how many slides are kept open, least recently used first out. At least 1; the default is 10.
    /// </summary>
    public int SlideCacheSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets how ICC color profiles are handled, for Deep Zoom tiles and OME-Zarr chunks alike. The default
    /// is <see cref="ColorMode.Default"/>, which converts each image (the slide and every associated image, each with
    /// its own profile) to sRGB when it can.
    /// </summary>
    public ColorMode ColorMode { get; set; } = ColorMode.Default;

    /// <summary>Gets the Deep Zoom settings.</summary>
    public DeepZoomServerOptions DeepZoom { get; } = new();

    /// <summary>Gets the OME-Zarr settings.</summary>
    public OmeZarrServerOptions OmeZarr { get; } = new();
}

/// <summary>Deep Zoom settings of <see cref="OpenSlideServerOptions"/>, matching the openslide-python examples.</summary>
public sealed class DeepZoomServerOptions
{
    /// <summary>Gets or sets the width and height of a tile, not counting overlap. At least 1; the default is 254.</summary>
    public int TileSize { get; set; } = 254;

    /// <summary>Gets or sets the extra pixels added to each interior edge of a tile. At least 0; the default is 1.</summary>
    public int Overlap { get; set; } = 1;

    /// <summary>
    /// Gets or sets the tile format written in the DZI. The default is <see cref="ImageFormat.Jpeg"/>. Tile URLs
    /// accept both <c>jpeg</c> and <c>png</c> whatever this is set to.
    /// </summary>
    public ImageFormat Format { get; set; } = ImageFormat.Jpeg;

    /// <summary>Gets or sets the JPEG quality, from 0 to 100. The default is 75.</summary>
    public int Quality { get; set; } = 75;

    /// <summary>
    /// Gets or sets a value indicating whether only the non-empty slide region is shown. The default is
    /// <see langword="true"/> (openslide-python's <c>--ignore-bounds</c> turns it off). Associated images are always
    /// shown whole.
    /// </summary>
    public bool LimitBounds { get; set; } = true;
}

/// <summary>OME-Zarr settings of <see cref="OpenSlideServerOptions"/>; see <see cref="OmeZarrOptions"/>.</summary>
public sealed class OmeZarrServerOptions
{
    /// <summary>Gets or sets the NGFF version, and with it the Zarr format. The default is <see cref="NgffVersion.V0_5"/>.</summary>
    public NgffVersion Version { get; set; } = NgffVersion.V0_5;

    /// <summary>Gets or sets the width and height of a chunk, from 16 to 8192. The default is 512.</summary>
    /// <remarks>
    /// Each open image keeps a render cache of up to <c>16 * ChunkSize * ChunkSize * 4</c> bytes (16 MiB with the
    /// default), so large chunks and a large <see cref="OpenSlideServerOptions.SlideCacheSize"/> add up.
    /// </remarks>
    public int ChunkSize { get; set; } = 512;

    /// <summary>Gets or sets how chunks are compressed. The default is <see cref="OmeZarrCompression.None"/>.</summary>
    public OmeZarrCompression Compression { get; set; } = OmeZarrCompression.None;

    /// <summary>Gets or sets the gzip level, from 0 to 9, used with <see cref="OmeZarrCompression.Gzip"/>. The default is 6.</summary>
    public int GzipLevel { get; set; } = 6;

    /// <summary>
    /// Gets or sets a value indicating whether only the non-empty slide region is exported. The default is
    /// <see langword="true"/>. Associated images are always exported whole.
    /// </summary>
    public bool LimitBounds { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether responses allow cross-origin reads
    /// (<c>Access-Control-Allow-Origin: *</c>), as web viewers such as vizarr need. The default is
    /// <see langword="true"/>.
    /// </summary>
    public bool AllowCrossOrigin { get; set; } = true;
}
