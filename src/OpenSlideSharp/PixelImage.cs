using System;

namespace OpenSlideSharp;

/// <summary>An image of 32-bit pixels in a managed byte array, stored row by row with no padding.</summary>
/// <remarks>
/// Pixels are bytes B, G, R, A in memory order; <see cref="Format"/> says whether the color channels are premultiplied.
/// The instance does not copy <see cref="Data"/> and exposes it for direct, zero-copy use, so callers own the array
/// they pass in and must not change it while others use the image.
/// </remarks>
public sealed class PixelImage
{
    /// <summary>Creates an image over an existing pixel array. The array is used as is, not copied.</summary>
    /// <param name="width">Width in pixels, not negative.</param>
    /// <param name="height">Height in pixels, not negative.</param>
    /// <param name="format">How the pixel bytes are to be interpreted.</param>
    /// <param name="data">Pixel bytes; its length must equal <c>width * height * 4</c>.</param>
    /// <param name="iccProfile">The ICC color profile the pixels are in, or empty if unknown.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is negative, <paramref name="width"/> times 4 exceeds <see cref="int.MaxValue"/>, or <paramref name="format"/> is not a defined value.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> has the wrong length.</exception>
    public PixelImage(int width, int height, PixelFormat format, byte[] data, ReadOnlyMemory<byte> iccProfile = default)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        }

        if (width > int.MaxValue / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width times 4 must not exceed Int32.MaxValue.");
        }

        if (format != PixelFormat.Bgra32Premultiplied && format != PixelFormat.Bgra32 && format != PixelFormat.Bgra32Opaque)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Undefined pixel format.");
        }

        long expected = (long)width * height * 4;
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"Data length {data.Length} does not match {width} x {height} x 4 = {expected} bytes.", nameof(data));
        }

        Width = width;
        Height = height;
        Format = format;
        Data = data;
        IccProfile = iccProfile;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets how the pixel bytes are to be interpreted.</summary>
    public PixelFormat Format { get; }

    /// <summary>Gets the pixel bytes (B, G, R, A per pixel, rows top to bottom). Not a copy.</summary>
    public byte[] Data { get; }

    /// <summary>Gets the ICC color profile of the pixels, or an empty value if there is none.</summary>
    public ReadOnlyMemory<byte> IccProfile { get; }

    /// <summary>Gets the number of bytes per row, which is <c>Width * 4</c>.</summary>
    public int Stride => Width * 4;

    /// <summary>Converts premultiplied alpha to straight alpha, matching openslide-python.</summary>
    /// <remarks>
    /// For each pixel with alpha <c>a</c>: if <c>a == 255</c> it is unchanged; if <c>a == 0</c> the color channels
    /// become 0; otherwise each color channel <c>c</c> becomes <c>255 * c / a</c> using truncating integer division.
    /// A premultiplied pixel with <c>c &gt; a</c> is invalid; such values are clamped to 255, whereas openslide-python
    /// wraps around as uint8. This difference is intentional.
    /// </remarks>
    /// <returns>
    /// A new <see cref="PixelFormat.Bgra32"/> image if <see cref="Format"/> is <see cref="PixelFormat.Bgra32Premultiplied"/>;
    /// otherwise this same instance. The source image is not modified.
    /// </returns>
    public PixelImage ToStraightAlpha()
    {
        if (Format != PixelFormat.Bgra32Premultiplied)
        {
            return this;
        }

        byte[] src = Data;
        byte[] dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            int a = src[i + 3];
            if (a == 255)
            {
                dst[i] = src[i];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = src[i + 2];
                dst[i + 3] = 255;
            }
            else if (a != 0)
            {
                dst[i] = Unpremultiply(src[i], a);
                dst[i + 1] = Unpremultiply(src[i + 1], a);
                dst[i + 2] = Unpremultiply(src[i + 2], a);
                dst[i + 3] = (byte)a;
            }
        }

        return new PixelImage(Width, Height, PixelFormat.Bgra32, dst, IccProfile);
    }

    /// <summary>Composites this image over a solid background color, giving an opaque image.</summary>
    /// <remarks>
    /// Uses the Pillow <c>paste</c> / openslide-python formula. For <see cref="PixelFormat.Bgra32Premultiplied"/> the
    /// color is un-premultiplied first; for <see cref="PixelFormat.Bgra32"/> the color is already straight, so it
    /// is blended directly. The ICC profile is kept unchanged.
    /// </remarks>
    /// <param name="red">Background red.</param>
    /// <param name="green">Background green.</param>
    /// <param name="blue">Background blue.</param>
    /// <returns>A new <see cref="PixelFormat.Bgra32Opaque"/> image, or this same instance if it is already opaque.</returns>
    public PixelImage ToOpaque(byte red, byte green, byte blue)
    {
        if (Format == PixelFormat.Bgra32Opaque)
        {
            return this;
        }

        byte[] dst = (byte[])Data.Clone();
        if (Format == PixelFormat.Bgra32Premultiplied)
        {
            Compositor.CompositeInPlace(dst, red, green, blue);
        }
        else
        {
            Compositor.BlendStraightInPlace(dst, red, green, blue);
        }

        return new PixelImage(Width, Height, PixelFormat.Bgra32Opaque, dst, IccProfile);
    }

    // Premultiplied input can have c > a (invalid); clamp so the byte cast never wraps.
    private static byte Unpremultiply(byte c, int a) => (byte)Math.Min(255, 255 * c / a);
}
