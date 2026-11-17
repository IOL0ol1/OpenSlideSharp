using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenSlideSharp.AspNetCore.Internal;
using OpenSlideSharp.SkiaSharp;

namespace OpenSlideSharp.AspNetCore;

/// <summary>
/// An open slide in the <see cref="SlideCache"/>, with the Deep Zoom and OME-Zarr generators for the slide and its
/// associated images. Get one through a <see cref="SlideLease"/>, and use it only while the lease is held.
/// </summary>
/// <remarks>
/// <para>
/// Each image (the slide itself, or one associated image) gets its color transform and its generators the first
/// time they are asked for, and keeps them for as long as the slide stays open. All members are thread-safe.
/// </para>
/// <para>
/// Color handling follows <see cref="OpenSlideServerOptions.ColorMode"/>, per image, with the image's own ICC
/// profile. An image without a profile is handled the same way in every mode.
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="ColorMode.Default"/>: when <see cref="SkiaColorTransform.Create"/> gives a transform, the generators
/// convert to sRGB and tiles are encoded with <see cref="ColorMode.Ignore"/>. When it cannot, Deep Zoom tiles are
/// encoded with <see cref="ColorMode.Default"/>, which embeds the profile (and drops one too large for a JPEG),
/// OME-Zarr chunks are left unconverted, and a warning is logged once for the image.
/// </item>
/// <item>
/// <see cref="ColorMode.Embed"/>: nothing is converted and tiles are encoded with <see cref="ColorMode.Embed"/>.
/// OME-Zarr cannot hold a profile, so its chunks are written as with <see cref="ColorMode.Ignore"/>, and a
/// warning is logged once for an image that has a profile.
/// </item>
/// <item><see cref="ColorMode.Ignore"/>: nothing is converted and profiles are dropped.</item>
/// </list>
/// </remarks>
public sealed class SlideEntry
{
    private readonly OpenSlideServerOptions _options;
    private readonly ILogger _logger;
    private readonly Lazy<ImageState> _slideImage;
    private readonly ConcurrentDictionary<string, Lazy<ImageState>> _associatedImages = new(StringComparer.Ordinal);

    internal SlideEntry(string filePath, OpenSlide slide, OpenSlideServerOptions options, ILogger logger)
    {
        FilePath = filePath;
        Slide = slide;
        _options = options;
        _logger = logger;
        _slideImage = new Lazy<ImageState>(() => new ImageState(this, null));
    }

    /// <summary>Gets the full path of the slide file.</summary>
    public string FilePath { get; }

    /// <summary>Gets the open slide. Do not dispose it; the cache does that once no lease uses it.</summary>
    public OpenSlide Slide { get; }

    /// <summary>Gets the Deep Zoom generator for the slide or one of its associated images.</summary>
    /// <param name="associatedName">The associated image name, or <see langword="null"/> for the slide itself.</param>
    /// <returns>The generator, created on first use and then reused.</returns>
    /// <exception cref="KeyNotFoundException">The slide has no associated image with that name.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public DeepZoomGenerator GetDeepZoom(string? associatedName) => GetImage(associatedName).DeepZoom.Value;

    /// <summary>Gets the OME-Zarr generator for the slide or one of its associated images.</summary>
    /// <param name="associatedName">The associated image name, or <see langword="null"/> for the slide itself.</param>
    /// <returns>The generator, created on first use and then reused.</returns>
    /// <exception cref="KeyNotFoundException">The slide has no associated image with that name.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public OmeZarrGenerator GetOmeZarr(string? associatedName) => GetImage(associatedName).OmeZarr.Value;

    /// <summary>
    /// Gets the color mode to encode the tiles of <see cref="GetDeepZoom"/> with, following the rules in the
    /// remarks of <see cref="SlideEntry"/>.
    /// </summary>
    /// <param name="associatedName">The associated image name, or <see langword="null"/> for the slide itself.</param>
    /// <returns>
    /// <see cref="ColorMode.Ignore"/>, <see cref="ColorMode.Embed"/>, or <see cref="ColorMode.Default"/> for an
    /// image whose profile cannot be converted (encoding then embeds it, as far as a JPEG can hold it).
    /// </returns>
    /// <exception cref="KeyNotFoundException">The slide has no associated image with that name.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    public ColorMode EncodeMode(string? associatedName) => GetImage(associatedName).EncodeMode;

    internal void Dispose() => Slide.Dispose();

    private ImageState GetImage(string? associatedName)
    {
        if (associatedName is null)
        {
            return _slideImage.Value;
        }

        if (_associatedImages.TryGetValue(associatedName, out Lazy<ImageState>? known))
        {
            return known.Value;
        }

        // Check the name first, so requests for made-up names cannot grow the dictionary.
        if (!Slide.AssociatedImages.TryGetValue(associatedName, out AssociatedImage? image))
        {
            throw new KeyNotFoundException($"The slide has no associated image named '{associatedName}'.");
        }

        return _associatedImages.GetOrAdd(
            associatedName,
            static (_, state) => new Lazy<ImageState>(() => new ImageState(state.Entry, state.Image)),
            (Entry: this, Image: image)).Value;
    }

    // The color decisions and generators of one image. Lazy<T> (ExecutionAndPublication) makes each of them, and
    // each warning, happen once.
    private sealed class ImageState
    {
        private readonly SlideEntry _entry;
        private readonly AssociatedImage? _image;
        private readonly bool _hasProfile;
        private readonly IColorTransform? _transform;

        public ImageState(SlideEntry entry, AssociatedImage? image)
        {
            _entry = entry;
            _image = image;
            ReadOnlyMemory<byte> profile = image is null ? entry.Slide.ColorProfile : image.ColorProfile;
            _hasProfile = !profile.IsEmpty;

            ColorMode mode = entry._options.ColorMode;
            if (!_hasProfile || mode == ColorMode.Ignore)
            {
                EncodeMode = ColorMode.Ignore;
            }
            else if (mode == ColorMode.Embed)
            {
                EncodeMode = ColorMode.Embed;
            }
            else
            {
                _transform = SkiaColorTransform.Create(profile);
                if (_transform is null)
                {
                    Log.CannotConvertProfile(entry._logger, entry.FilePath, Describe());

                    // Default encoding embeds the profile too, and drops one a JPEG cannot hold instead of throwing.
                    EncodeMode = ColorMode.Default;
                }
                else
                {
                    EncodeMode = ColorMode.Ignore;
                }
            }

            DeepZoom = new Lazy<DeepZoomGenerator>(CreateDeepZoom);
            OmeZarr = new Lazy<OmeZarrGenerator>(CreateOmeZarr);
        }

        public ColorMode EncodeMode { get; }

        public Lazy<DeepZoomGenerator> DeepZoom { get; }

        public Lazy<OmeZarrGenerator> OmeZarr { get; }

        private DeepZoomGenerator CreateDeepZoom()
        {
            DeepZoomServerOptions options = _entry._options.DeepZoom;
            return _image is null
                ? new DeepZoomGenerator(_entry.Slide, options.TileSize, options.Overlap, options.LimitBounds, _transform)
                : new DeepZoomGenerator(_image, options.TileSize, options.Overlap, _transform);
        }

        private OmeZarrGenerator CreateOmeZarr()
        {
            if (_hasProfile && _entry._options.ColorMode == ColorMode.Embed)
            {
                Log.OmeZarrCannotEmbedProfile(_entry._logger, _entry.FilePath, Describe());
            }

            OmeZarrServerOptions server = _entry._options.OmeZarr;
            var options = new OmeZarrOptions
            {
                Version = server.Version,
                ChunkSize = server.ChunkSize,
                Compression = server.Compression,
                GzipLevel = server.GzipLevel,
                RenderCacheSize = server.RenderCacheSize,

                // An associated image has no bounds of its own; Deep Zoom shows it whole too.
                LimitBounds = _image is null && server.LimitBounds,
                ColorTransform = _transform,
            };
            return _image is null ? new OmeZarrGenerator(_entry.Slide, options) : new OmeZarrGenerator(_image, options);
        }

        private string Describe() => _image is null ? "the slide" : $"associated image '{_image.Name}'";
    }
}
