using System;
using System.Buffers;
using System.Diagnostics;

namespace OpenSlideSharp;

/// <summary>
/// Renders rectangles of a <see cref="SlideGeometry"/> view as opaque BGRA: read the mapped region, composite it
/// over the background, resize it to the requested size and optionally convert its colors. Stateless apart from
/// its immutable inputs, so one instance may render on several threads at once.
/// </summary>
internal sealed class RegionRenderer
{
    private readonly IRenderSource _source;
    private readonly SlideGeometry _geometry;
    private readonly IColorTransform? _colorTransform;

    internal RegionRenderer(IRenderSource source, SlideGeometry geometry, IColorTransform? colorTransform)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _colorTransform = colorTransform;
    }

    /// <summary>
    /// Renders the rectangle (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>,
    /// <paramref name="height"/>) of the view downsampled by <c>2^scaleExponent</c> into the first
    /// <c>width * height * 4</c> bytes of <paramref name="destination"/>, as opaque BGRA.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The scale exponent, width or height is out of range, or the slide region to read would not fit in a .NET array.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    internal void Render(int scaleExponent, long x, long y, int width, int height, Span<byte> destination)
    {
        Span<byte> output = OutputSpan(width, height, destination);
        SourceRegion region = _geometry.Map(scaleExponent, x, y, width, height);
        RenderRegion(_source, region, _geometry.Background, _colorTransform, width, height, output);
    }

    /// <summary>
    /// Reads <paramref name="region"/> from <paramref name="source"/>, composites it over
    /// <paramref name="background"/>, resizes it to <paramref name="width"/> x <paramref name="height"/> with
    /// Pillow's Lanczos filter if its size differs, and applies <paramref name="colorTransform"/> if there is one.
    /// A region with no pixels gives a solid background. <paramref name="destination"/> must hold exactly
    /// <c>width * height * 4</c> bytes.
    /// </summary>
    internal static void RenderRegion(
        IRenderSource source,
        SourceRegion region,
        (byte Red, byte Green, byte Blue) background,
        IColorTransform? colorTransform,
        int width,
        int height,
        Span<byte> destination)
    {
        Debug.Assert(destination.Length == (long)width * height * 4, "destination must be exactly the output size");
        if (destination.IsEmpty)
        {
            return;
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            Fill(destination, background);
        }
        else
        {
            // Rejects regions too large for a .NET array, with their size in the message, before reading anything.
            int regionBytes = (int)OpenSlide.CheckedPixelBytes(region.Width, region.Height, "slide region");
            int regionWidth = (int)region.Width;
            int regionHeight = (int)region.Height;
            if (regionWidth == width && regionHeight == height)
            {
                source.ReadRegion(region.X, region.Y, region.Level, regionWidth, regionHeight, destination);
                Compositor.CompositeInPlace(destination, background.Red, background.Green, background.Blue);
            }
            else
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(regionBytes);
                try
                {
                    Span<byte> pixels = buffer.AsSpan(0, regionBytes);
                    source.ReadRegion(region.X, region.Y, region.Level, regionWidth, regionHeight, pixels);
                    Compositor.CompositeInPlace(pixels, background.Red, background.Green, background.Blue);
                    LanczosResampler.Resize(pixels, regionWidth, regionHeight, destination, width, height);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        colorTransform?.Apply(destination, width, height);
    }

    /// <summary>Validates an output size and returns the part of <paramref name="destination"/> it covers.</summary>
    internal static Span<byte> OutputSpan(int width, int height, Span<byte> destination)
    {
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
                $"Destination holds {destination.Length} bytes but a {width} x {height} image needs {length}.",
                nameof(destination));
        }

        return destination.Slice(0, (int)length);
    }

    private static void Fill(Span<byte> bgra, (byte Red, byte Green, byte Blue) color)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = color.Blue;
            bgra[i + 1] = color.Green;
            bgra[i + 2] = color.Red;
            bgra[i + 3] = 255;
        }
    }
}
