using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenSlideSharp;

/// <summary>
/// Renders from an <see cref="AssociatedImage"/>, like openslide-python's <c>ImageSlide</c>: one level with
/// downsample 1, no properties, and the image's own ICC profile.
/// </summary>
internal sealed class AssociatedImageRenderSource : IRenderSource
{
    private static readonly IReadOnlyList<double> SingleDownsample = Array.AsReadOnly(new[] { 1.0 });

    private readonly AssociatedImage _image;
    private readonly IReadOnlyList<SlideSize> _levelDimensions;
    private readonly object _readLock = new();
    private byte[]? _pixels; // premultiplied BGRA of the whole image; null until the first read

    internal AssociatedImageRenderSource(AssociatedImage image)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _levelDimensions = Array.AsReadOnly(new[] { image.Dimensions });
    }

    /// <summary>
    /// Creates the source for a public entry point: checks <paramref name="image"/> for <see langword="null"/>
    /// and then that its slide is usable, so callers report the public parameter name and fail before any other work.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The slide has been disposed.</exception>
    /// <exception cref="OpenSlideException">The slide is in the error state.</exception>
    internal static AssociatedImageRenderSource Create(AssociatedImage image)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        image.Slide.ThrowIfUnusable();
        return new AssociatedImageRenderSource(image);
    }

    public IReadOnlyList<SlideSize> LevelDimensions => _levelDimensions;

    public IReadOnlyList<double> LevelDownsamples => SingleDownsample;

    public ReadOnlyMemory<byte> ColorProfile => _image.ColorProfile;

    public int GetBestLevelForDownsample(double downsample) => 0;

    public string? GetProperty(string name) => null;

    /// <summary>
    /// Copies the part of the region inside the image and makes the rest transparent, with the same result as
    /// <c>ImageSlide.read_region</c>. The whole image is read on first use and cached.
    /// </summary>
    public void ReadRegion(long x, long y, int level, int width, int height, Span<byte> destination)
    {
        if (level != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "An associated image has only level 0.");
        }

        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        }

        long length = (long)width * height * 4;
        if (destination.Length < length)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} bytes but a {width} x {height} region needs {length}.",
                nameof(destination));
        }

        // Like every AssociatedImage member, fail once the slide is disposed or in the error state, cached or not.
        _image.Slide.ThrowIfUnusable();
        byte[] pixels = GetPixels();
        Span<byte> target = destination.Slice(0, (int)length);
        target.Clear();

        long imageWidth = _levelDimensions[0].Width;
        long imageHeight = _levelDimensions[0].Height;
        if (x >= imageWidth || y >= imageHeight)
        {
            return;
        }

        // x < imageWidth <= int.MaxValue here, so x + width cannot overflow.
        long left = Math.Max(x, 0);
        long top = Math.Max(y, 0);
        long right = Math.Min(x + width, imageWidth);
        long bottom = Math.Min(y + height, imageHeight);
        if (left >= right || top >= bottom)
        {
            return;
        }

        int rowBytes = (int)(right - left) * 4;
        for (long row = top; row < bottom; row++)
        {
            int sourceOffset = (int)(((row * imageWidth) + left) * 4);
            int targetOffset = (int)((((row - y) * width) + (left - x)) * 4);
            pixels.AsSpan(sourceOffset, rowBytes).CopyTo(target.Slice(targetOffset, rowBytes));
        }
    }

    public string Describe() => _image.ToString();

    private byte[] GetPixels()
    {
        byte[]? pixels = Volatile.Read(ref _pixels);
        if (pixels is null)
        {
            // A lock rather than Lazy<T>: the image is read only once, and a failed read is not cached.
            lock (_readLock)
            {
                pixels = _pixels;
                if (pixels is null)
                {
                    pixels = _image.Read().Data;
                    Volatile.Write(ref _pixels, pixels);
                }
            }
        }

        return pixels;
    }
}
