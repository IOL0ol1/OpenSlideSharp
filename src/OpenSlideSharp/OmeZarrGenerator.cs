using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace OpenSlideSharp;

/// <summary>The OME-NGFF (OME-Zarr) metadata version, which also sets the Zarr format.</summary>
public enum NgffVersion
{
    /// <summary>
    /// NGFF 0.4 on Zarr v2: <c>.zgroup</c>, <c>.zattrs</c> and <c>{level}/.zarray</c>, with chunks at
    /// <c>{level}/{c}/{y}/{x}</c>.
    /// </summary>
    V0_4,

    /// <summary>
    /// NGFF 0.5 on Zarr v3: <c>zarr.json</c> for the group and <c>{level}/zarr.json</c> for each array, with chunks
    /// at <c>{level}/c/{c}/{y}/{x}</c>. The default.
    /// </summary>
    V0_5,
}

/// <summary>How OME-Zarr chunks are compressed.</summary>
public enum OmeZarrCompression
{
    /// <summary>Chunks are the raw bytes.</summary>
    None,

    /// <summary>Chunks are gzip streams (the Zarr <c>gzip</c> codec or compressor).</summary>
    Gzip,
}

/// <summary>
/// Settings for <see cref="OmeZarrGenerator"/>. The generator copies them when it is created, so changing an
/// instance later does not affect generators already made from it. Setters reject out-of-range values.
/// </summary>
public sealed class OmeZarrOptions
{
    private const int MinChunkSize = 16;
    private const int MaxChunkSize = 8192;

    private NgffVersion _version = NgffVersion.V0_5;
    private int _chunkSize = 512;
    private OmeZarrCompression _compression = OmeZarrCompression.None;
    private int _gzipLevel = 6;
    private int _renderCacheSize = 16;

    /// <summary>Gets or sets the NGFF version, and with it the Zarr format. The default is <see cref="NgffVersion.V0_5"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="NgffVersion"/>.</exception>
    public NgffVersion Version
    {
        get => _version;
        set
        {
            if (value != NgffVersion.V0_4 && value != NgffVersion.V0_5)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown NGFF version.");
            }

            _version = value;
        }
    }

    /// <summary>
    /// Gets or sets the width and height of a chunk, from 16 to 8192. The default is 512. Each chunk holds one
    /// channel, so its shape is <c>1 x ChunkSize x ChunkSize</c>. Large chunks make the render cache large too;
    /// see <see cref="RenderCacheSize"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 16 or greater than 8192.</exception>
    public int ChunkSize
    {
        get => _chunkSize;
        set
        {
            if (value < MinChunkSize || value > MaxChunkSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, $"The chunk size must be from {MinChunkSize} to {MaxChunkSize}.");
            }

            _chunkSize = value;
        }
    }

    /// <summary>Gets or sets how chunks are compressed. The default is <see cref="OmeZarrCompression.None"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="OmeZarrCompression"/>.</exception>
    public OmeZarrCompression Compression
    {
        get => _compression;
        set
        {
            if (value != OmeZarrCompression.None && value != OmeZarrCompression.Gzip)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown compression.");
            }

            _compression = value;
        }
    }

    /// <summary>
    /// Gets or sets the gzip level, from 0 (store only) to 9 (smallest), used with
    /// <see cref="OmeZarrCompression.Gzip"/>. The default is 6. On .NET 10 the level is passed to zlib as is; on
    /// .NET Standard, 0 maps to <see cref="CompressionLevel.NoCompression"/>, 1 to 5 to
    /// <see cref="CompressionLevel.Fastest"/> and 6 to 9 to <see cref="CompressionLevel.Optimal"/>. The metadata
    /// always states the level set here.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 0 or greater than 9.</exception>
    public int GzipLevel
    {
        get => _gzipLevel;
        set
        {
            if (value < 0 || value > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The gzip level must be from 0 to 9.");
            }

            _gzipLevel = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether only the non-empty slide region (the <c>openslide.bounds-*</c>
    /// properties) is exported. If so, every dataset gets a translation that places it at its position in the
    /// full slide. The default is <see langword="false"/>.
    /// </summary>
    public bool LimitBounds { get; set; }

    /// <summary>
    /// Gets or sets the transform that converts rendered pixels to sRGB, or <see langword="null"/> to write the
    /// slide pixels as they are. The generator keeps a reference to the transform, not a copy.
    /// </summary>
    public IColorTransform? ColorTransform { get; set; }

    /// <summary>
    /// Gets or sets how many rendered chunk regions are kept, least recently used first out, so the three channels
    /// of a chunk are rendered once. 0 turns the cache off. The default is 16.
    /// </summary>
    /// <remarks>
    /// Each entry holds the BGRA pixels of one chunk, so a generator can keep up to about
    /// <c>RenderCacheSize * ChunkSize * ChunkSize * 4</c> bytes alive for as long as it exists: 16 MiB with the
    /// defaults, but 4 GiB with a <see cref="ChunkSize"/> of 8192 and 16 entries. Lower this value when using large
    /// chunks or many generators. Reading the keys in <see cref="OmeZarrGenerator.EnumerateKeys"/> order needs
    /// only one entry.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int RenderCacheSize
    {
        get => _renderCacheSize;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The render cache size must not be negative.");
            }

            _renderCacheSize = value;
        }
    }
}

/// <summary>
/// Exposes a slide, or an associated image, as an OME-Zarr image: a multiscale <c>uint8</c> array with axes
/// <c>c, y, x</c> and three channels (red, green, blue, composited over the slide background), as NGFF 0.5 on
/// Zarr v3 (the default) or NGFF 0.4 on Zarr v2. Nothing is written to disk: callers serve or save the entries
/// that <see cref="EnumerateKeys"/> lists and <see cref="TryGetEntry"/> returns, for example over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Level 0 is full resolution and each further level halves the previous one, rounding up, until a level fits
/// in a single chunk; an associated image has one level. Pixels are rendered with the same pipeline as
/// <see cref="DeepZoomGenerator"/> (Lanczos-3 resampling from the best OpenSlide level), so chunk (y, x) of a
/// level equals the matching tile of a Deep Zoom generator with a tile size of <see cref="ChunkSize"/> and no
/// overlap. Edge chunks are padded with zeros.
/// </para>
/// <para>
/// Scales are in micrometers when the slide has valid <c>openslide.mpp-x</c> and <c>openslide.mpp-y</c>
/// values, and in level 0 pixels otherwise (also when only one of them is valid).
/// </para>
/// <para>
/// The generator never logs. OME-Zarr has no place for an ICC profile, so when the slide has one and no
/// <see cref="OmeZarrOptions.ColorTransform"/> is given, the pixels are written unconverted and the profile is
/// dropped (the equivalent of <c>ColorMode.Ignore</c>); callers such as web hosts and tools are responsible for
/// warning about that.
/// </para>
/// <para>
/// Metadata is built in the constructor. Instances are thread-safe: <see cref="GetChunk"/> and
/// <see cref="TryGetEntry"/> may be called from several threads at once.
/// </para>
/// </remarks>
public sealed class OmeZarrGenerator
{
    private const int ChannelCount = 3;

    private readonly IRenderSource _source;
    private readonly Action? _ensureUsable;
    private readonly RegionRenderer _renderer;
    private readonly RenderCache? _cache;
    private readonly OmeZarrCompression _compression;
    private readonly int _gzipLevel;
    private readonly bool _limitBounds;
    private readonly SlideSize[] _levelDimensions;
    private readonly long[] _columns;
    private readonly long[] _rows;
    private readonly string[] _metadataKeys;
    private readonly Dictionary<string, byte[]> _metadata;

    /// <summary>Creates a generator for a slide.</summary>
    /// <param name="slide">The slide.</param>
    /// <param name="options">The settings, or <see langword="null"/> for the defaults. They are copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="slide"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public OmeZarrGenerator(OpenSlide slide, OmeZarrOptions? options = null)
        : this(SlideRenderSource.Create(slide), Path.GetFileName(slide.FileName), false, options, slide.ThrowIfUnusable)
    {
    }

    /// <summary>Creates a generator for an associated image, which always has a single level.</summary>
    /// <param name="image">The associated image; its <see cref="AssociatedImage.Name"/> becomes the image name.</param>
    /// <param name="options">The settings, or <see langword="null"/> for the defaults. They are copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public OmeZarrGenerator(AssociatedImage image, OmeZarrOptions? options = null)
        : this(AssociatedImageRenderSource.Create(image), image.Name, true, options, image.Slide.ThrowIfUnusable)
    {
    }

    internal OmeZarrGenerator(IRenderSource source, string name, bool singleLevel, OmeZarrOptions? options)
        : this(source, name, singleLevel, options, null)
    {
    }

    private OmeZarrGenerator(
        IRenderSource source, string name, bool singleLevel, OmeZarrOptions? options, Action? ensureUsable)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        // Copy every setting once, so later changes to the options object do not reach this generator.
        options ??= new OmeZarrOptions();
        _source = source;
        _ensureUsable = ensureUsable;
        Version = options.Version;
        ChunkSize = options.ChunkSize;
        _compression = options.Compression;
        _gzipLevel = options.GzipLevel;
        _limitBounds = options.LimitBounds;
        IColorTransform? colorTransform = options.ColorTransform;
        int renderCacheSize = options.RenderCacheSize;

        var geometry = new SlideGeometry(source, _limitBounds);
        _renderer = new RegionRenderer(source, geometry, colorTransform);
        if (renderCacheSize > 0)
        {
            _cache = new RenderCache(renderCacheSize, RenderRegion);
        }

        // Halve until a level fits in one chunk; the 1 x 1 view always does, so the loop ends within the views.
        var levels = new List<SlideSize>();
        for (int exponent = 0; exponent < geometry.LevelCount; exponent++)
        {
            SlideSize size = geometry.GetLevelSize(exponent);
            levels.Add(size);
            if (singleLevel || (size.Width <= ChunkSize && size.Height <= ChunkSize))
            {
                break;
            }
        }

        _levelDimensions = levels.ToArray();
        LevelDimensions = Array.AsReadOnly(_levelDimensions);
        _columns = new long[_levelDimensions.Length];
        _rows = new long[_levelDimensions.Length];
        for (int level = 0; level < _levelDimensions.Length; level++)
        {
            _columns[level] = GridMath.CeilDiv(_levelDimensions[level].Width, ChunkSize);
            _rows[level] = GridMath.CeilDiv(_levelDimensions[level].Height, ChunkSize);
        }

        (_metadataKeys, _metadata) = BuildMetadata(name, geometry);
    }

    /// <summary>Gets the number of levels.</summary>
    public int LevelCount => _levelDimensions.Length;

    /// <summary>Gets the pixel size of each level; level 0 is full resolution.</summary>
    public IReadOnlyList<SlideSize> LevelDimensions { get; }

    /// <summary>Gets the NGFF version, which also sets the Zarr format and the key layout.</summary>
    public NgffVersion Version { get; }

    /// <summary>Gets the width and height of a chunk.</summary>
    public int ChunkSize { get; }

    /// <summary>
    /// Renders one channel of one chunk and returns it as the store holds it: <c>ChunkSize * ChunkSize</c> bytes,
    /// row by row, with zeros outside the level, gzip-compressed if <see cref="OmeZarrOptions.Compression"/> says so.
    /// </summary>
    /// <param name="level">The level; 0 is full resolution.</param>
    /// <param name="channel">The channel: 0 for red, 1 for green, 2 for blue.</param>
    /// <param name="chunkY">The chunk row within the level.</param>
    /// <param name="chunkX">The chunk column within the level.</param>
    /// <returns>A new array the caller owns.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level, channel, chunk row or chunk column is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">Reading failed.</exception>
    public byte[] GetChunk(int level, int channel, int chunkY, int chunkX)
    {
        // Check first: cached regions and empty regions never reach the native slide.
        _ensureUsable?.Invoke();
        if (level < 0 || level >= LevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"The level must be from 0 to {LevelCount - 1}.");
        }

        if (channel < 0 || channel >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "The channel must be 0 (red), 1 (green) or 2 (blue).");
        }

        if (chunkY < 0 || chunkY >= _rows[level])
        {
            throw new ArgumentOutOfRangeException(nameof(chunkY), chunkY, $"Level {level} has {_rows[level]} chunk rows.");
        }

        if (chunkX < 0 || chunkX >= _columns[level])
        {
            throw new ArgumentOutOfRangeException(nameof(chunkX), chunkX, $"Level {level} has {_columns[level]} chunk columns.");
        }

        var key = new RegionKey(level, chunkY, chunkX);
        byte[] bgra = _cache is null ? RenderRegion(key) : _cache.GetOrRender(key);

        int size = ChunkSize;
        (int width, int height) = RegionSize(key);
        var plane = new byte[size * size];
        int offset = 2 - channel; // BGRA byte holding R, G or B
        for (int row = 0; row < height; row++)
        {
            int source = (row * width * 4) + offset;
            int target = row * size;
            for (int column = 0; column < width; column++, source += 4)
            {
                plane[target + column] = bgra[source];
            }
        }

        return _compression == OmeZarrCompression.Gzip ? Gzip(plane) : plane;
    }

    /// <summary>
    /// Returns the content of a store entry: a metadata document or a chunk. Keys are exactly those
    /// <see cref="EnumerateKeys"/> lists, with numbers in plain decimal; anything else, such as other Zarr
    /// versions' keys, consolidated metadata, empty segments, <c>..</c> or backslashes, is not found.
    /// </summary>
    /// <param name="key">The key, relative to the store root, for example <c>zarr.json</c> or <c>0/c/1/2/3</c>.</param>
    /// <param name="content">The content, in a new array the caller owns; <see langword="null"/> if not found.</param>
    /// <returns><see langword="true"/> if the store has the entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The key names a chunk and the slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The key names a chunk and reading it failed.</exception>
    public bool TryGetEntry(string key, [NotNullWhen(true)] out byte[]? content)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (_metadata.TryGetValue(key, out byte[]? metadata))
        {
            content = (byte[])metadata.Clone();
            return true;
        }

        if (TryParseChunkKey(key, out int level, out int channel, out int chunkY, out int chunkX))
        {
            content = GetChunk(level, channel, chunkY, chunkX);
            return true;
        }

        content = null;
        return false;
    }

    /// <summary>
    /// Lists every key of the store: first the metadata documents (group, then each level), then the chunks by
    /// level, row and column, with the three channels of a chunk next to each other. Fetching the entries in this
    /// order renders each chunk region once, even with a <see cref="OmeZarrOptions.RenderCacheSize"/> of 1.
    /// </summary>
    /// <returns>The keys, generated lazily.</returns>
    public IEnumerable<string> EnumerateKeys()
    {
        foreach (string key in _metadataKeys)
        {
            yield return key;
        }

        string prefix = Version == NgffVersion.V0_5 ? "/c/" : "/";
        for (int level = 0; level < LevelCount; level++)
        {
            for (long y = 0; y < _rows[level]; y++)
            {
                for (long x = 0; x < _columns[level]; x++)
                {
                    string position = "/" + Invariant(y) + "/" + Invariant(x);
                    for (int channel = 0; channel < ChannelCount; channel++)
                    {
                        yield return Invariant(level) + prefix + Invariant(channel) + position;
                    }
                }
            }
        }
    }

    /// <summary>Describes the generator and its settings.</summary>
    /// <returns>
    /// For example <c>OmeZarrGenerator(OpenSlide(path), version: V0_5, chunkSize: 512, compression: None, limitBounds: False)</c>.
    /// </returns>
    public override string ToString()
    {
        string compression = _compression == OmeZarrCompression.Gzip ? $"Gzip({_gzipLevel})" : "None";
        return $"OmeZarrGenerator({_source.Describe()}, version: {Version}, chunkSize: {ChunkSize}, " +
            $"compression: {compression}, limitBounds: {(_limitBounds ? "True" : "False")})";
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    // A decimal index with no sign, no leading zero (other than "0" itself) and no non-ASCII digits, within int.
    private static bool TryParseIndex(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 10 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        long result = 0;
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            result = (result * 10) + (c - '0');
        }

        if (result > int.MaxValue)
        {
            return false;
        }

        value = (int)result;
        return true;
    }

    private static GZipStream CreateGzipStream(Stream output, int level)
    {
#if NET
        return new GZipStream(output, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true);
#else
        CompressionLevel compressionLevel = level == 0 ? CompressionLevel.NoCompression
            : level <= 5 ? CompressionLevel.Fastest
            : CompressionLevel.Optimal;
        return new GZipStream(output, compressionLevel, leaveOpen: true);
#endif
    }

    private byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (GZipStream gzip = CreateGzipStream(output, _gzipLevel))
        {
            gzip.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    // Only chunks that exist: the metadata keys are looked up as they are, before this.
    private bool TryParseChunkKey(string key, out int level, out int channel, out int chunkY, out int chunkX)
    {
        level = channel = chunkY = chunkX = 0;
        string[] parts = key.Split('/');
        int first = Version == NgffVersion.V0_5 ? 2 : 1; // V0_5 keys have a "c" segment after the level
        if (parts.Length != first + 3 || (Version == NgffVersion.V0_5 && parts[1] != "c"))
        {
            return false;
        }

        return TryParseIndex(parts[0], out level) && level < LevelCount &&
            TryParseIndex(parts[first], out channel) && channel < ChannelCount &&
            TryParseIndex(parts[first + 1], out chunkY) && chunkY < _rows[level] &&
            TryParseIndex(parts[first + 2], out chunkX) && chunkX < _columns[level];
    }

    private (int Width, int Height) RegionSize(RegionKey key)
    {
        SlideSize size = _levelDimensions[key.Level];
        long x = key.X * ChunkSize;
        long y = key.Y * ChunkSize;
        return ((int)Math.Min(ChunkSize, size.Width - x), (int)Math.Min(ChunkSize, size.Height - y));
    }

    // The part of the chunk inside the level, as opaque BGRA.
    private byte[] RenderRegion(RegionKey key)
    {
        (int width, int height) = RegionSize(key);
        var bgra = new byte[width * height * 4];
        _renderer.Render(key.Level, key.X * ChunkSize, key.Y * ChunkSize, width, height, bgra);
        return bgra;
    }

    private (string[] Keys, Dictionary<string, byte[]> Content) BuildMetadata(string name, SlideGeometry geometry)
    {
        var keys = new List<string>();
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Add(string key, JsonText json)
        {
            keys.Add(key);
            content.Add(key, json.ToUtf8());
        }

        if (Version == NgffVersion.V0_5)
        {
            var group = new JsonText().StartObject()
                .Property("zarr_format", 3)
                .Property("node_type", "group")
                .Name("attributes").StartObject()
                .Name("ome").StartObject()
                .Property("version", "0.5");
            WriteImageAttributes(group, null, name, geometry);
            Add("zarr.json", group.EndObject().EndObject().EndObject());

            for (int level = 0; level < LevelCount; level++)
            {
                SlideSize size = _levelDimensions[level];
                var array = new JsonText().StartObject()
                    .Property("zarr_format", 3)
                    .Property("node_type", "array")
                    .Name("shape").Numbers(ChannelCount, size.Height, size.Width)
                    .Property("data_type", "uint8")
                    .Name("chunk_grid").StartObject()
                    .Property("name", "regular")
                    .Name("configuration").StartObject().Name("chunk_shape").Numbers(1, ChunkSize, ChunkSize).EndObject()
                    .EndObject()
                    .Name("chunk_key_encoding").StartObject()
                    .Property("name", "default")
                    .Name("configuration").StartObject().Property("separator", "/").EndObject()
                    .EndObject()
                    .Property("fill_value", 0)
                    .Name("codecs").StartArray()
                    .StartObject()
                    .Property("name", "bytes")
                    .Name("configuration").StartObject().Property("endian", "little").EndObject()
                    .EndObject();
                if (_compression == OmeZarrCompression.Gzip)
                {
                    array.StartObject()
                        .Property("name", "gzip")
                        .Name("configuration").StartObject().Property("level", _gzipLevel).EndObject()
                        .EndObject();
                }

                array.EndArray()
                    .Name("dimension_names").StartArray().Value("c").Value("y").Value("x").EndArray()
                    .EndObject();
                Add(Invariant(level) + "/zarr.json", array);
            }
        }
        else
        {
            Add(".zgroup", new JsonText().StartObject().Property("zarr_format", 2).EndObject());

            var attributes = new JsonText().StartObject();
            WriteImageAttributes(attributes, "0.4", name, geometry);
            Add(".zattrs", attributes.EndObject());

            for (int level = 0; level < LevelCount; level++)
            {
                SlideSize size = _levelDimensions[level];
                var array = new JsonText().StartObject()
                    .Name("chunks").Numbers(1, ChunkSize, ChunkSize)
                    .Name("compressor");
                if (_compression == OmeZarrCompression.Gzip)
                {
                    array.StartObject().Property("id", "gzip").Property("level", _gzipLevel).EndObject();
                }
                else
                {
                    array.Null();
                }

                array.Property("dimension_separator", "/")
                    .Property("dtype", "|u1")
                    .Property("fill_value", 0)
                    .Name("filters").Null()
                    .Property("order", "C")
                    .Name("shape").Numbers(ChannelCount, size.Height, size.Width)
                    .Property("zarr_format", 2)
                    .EndObject();
                Add(Invariant(level) + "/.zarray", array);
            }
        }

        return (keys.ToArray(), content);
    }

    // Writes the "multiscales" and "omero" members of the image attributes; version is the 0.4 per-multiscale
    // version, or null for 0.5, which keeps it next to them instead.
    private void WriteImageAttributes(JsonText json, string? version, string name, SlideGeometry geometry)
    {
        // Micrometers need both mpp values, and every number they give must be finite; otherwise level 0 pixels.
        double pixelX = 1;
        double pixelY = 1;
        bool micrometers = false;
        if (geometry.MppX is double mppX && geometry.MppY is double mppY)
        {
            double largest = Math.Pow(2, LevelCount - 1);
            micrometers = IsFinite(mppX * largest) && IsFinite(mppY * largest) &&
                IsFinite(geometry.OffsetX * mppX) && IsFinite(geometry.OffsetY * mppY);
            if (micrometers)
            {
                pixelX = mppX;
                pixelY = mppY;
            }
        }

        json.Name("multiscales").StartArray().StartObject();
        if (version is not null)
        {
            json.Property("version", version);
        }

        json.Property("name", name)
            .Property("type", "lanczos")
            .Name("metadata").StartObject()
            .Property("method", "OpenSlideSharp")
            .Property("description", "Rendered from OpenSlide levels with Lanczos-3 resampling")
            .EndObject();

        json.Name("axes").StartArray()
            .StartObject().Property("name", "c").Property("type", "channel").EndObject();
        foreach (string axis in new[] { "y", "x" })
        {
            json.StartObject().Property("name", axis).Property("type", "space");
            if (micrometers)
            {
                json.Property("unit", "micrometer");
            }

            json.EndObject();
        }

        json.EndArray();

        json.Name("datasets").StartArray();
        double downsample = 1; // 2^level, exact
        for (int level = 0; level < LevelCount; level++, downsample *= 2)
        {
            json.StartObject()
                .Property("path", Invariant(level))
                .Name("coordinateTransformations").StartArray()
                .StartObject()
                .Property("type", "scale")
                .Name("scale").Numbers(1.0, pixelY * downsample, pixelX * downsample)
                .EndObject();
            if (_limitBounds)
            {
                json.StartObject()
                    .Property("type", "translation")
                    .Name("translation").Numbers(0.0, geometry.OffsetY * pixelY, geometry.OffsetX * pixelX)
                    .EndObject();
            }

            json.EndArray().EndObject();
        }

        json.EndArray().EndObject().EndArray();

        json.Name("omero").StartObject().Name("channels").StartArray();
        foreach ((string label, string color) in new[] { ("Red", "FF0000"), ("Green", "00FF00"), ("Blue", "0000FF") })
        {
            json.StartObject()
                .Property("label", label)
                .Property("color", color)
                .Name("window").StartObject()
                .Property("start", 0).Property("end", 255).Property("min", 0).Property("max", 255)
                .EndObject()
                .Property("active", true)
                .Property("coefficient", 1)
                .Property("family", "linear")
                .Property("inverted", false)
                .EndObject();
        }

        json.EndArray()
            .Name("rdefs").StartObject().Property("model", "color").EndObject()
            .EndObject();
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private readonly record struct RegionKey(int Level, long Y, long X);

    /// <summary>
    /// A thread-safe least-recently-used cache of rendered regions. Each value is a <see cref="Lazy{T}"/> with
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>, so concurrent requests for one region render it
    /// once; a render that throws is removed so that the next request tries again.
    /// </summary>
    private sealed class RenderCache
    {
        private readonly int _capacity;
        private readonly Func<RegionKey, byte[]> _render;
        private readonly object _lock = new();
        private readonly Dictionary<RegionKey, LinkedListNode<(RegionKey Key, Lazy<byte[]> Value)>> _entries = new();
        private readonly LinkedList<(RegionKey Key, Lazy<byte[]> Value)> _recency = new(); // most recent first

        internal RenderCache(int capacity, Func<RegionKey, byte[]> render)
        {
            _capacity = capacity;
            _render = render;
        }

        internal byte[] GetOrRender(RegionKey key)
        {
            Lazy<byte[]> value;
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var node))
                {
                    _recency.Remove(node);
                    _recency.AddFirst(node);
                    value = node.Value.Value;
                }
                else
                {
                    value = new Lazy<byte[]>(() => _render(key), LazyThreadSafetyMode.ExecutionAndPublication);
                    _entries.Add(key, _recency.AddFirst((key, value)));
                    if (_entries.Count > _capacity)
                    {
                        var oldest = _recency.Last!;
                        _recency.RemoveLast();
                        _entries.Remove(oldest.Value.Key);
                    }
                }
            }

            try
            {
                return value.Value;
            }
            catch
            {
                lock (_lock)
                {
                    if (_entries.TryGetValue(key, out var node) && ReferenceEquals(node.Value.Value, value))
                    {
                        _recency.Remove(node);
                        _entries.Remove(key);
                    }
                }

                throw;
            }
        }
    }
}
