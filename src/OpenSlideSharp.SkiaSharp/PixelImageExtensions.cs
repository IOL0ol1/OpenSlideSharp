using System;
using System.Runtime.InteropServices;
using OpenSlideSharp.SkiaSharp.Internal;
using SkiaSharp;

namespace OpenSlideSharp.SkiaSharp;

/// <summary>SkiaSharp conversion and encoding for <see cref="PixelImage"/>.</summary>
public static class PixelImageExtensions
{
    /// <summary>Copies the pixels into a new <see cref="SKBitmap"/> (Bgra8888, no color space).</summary>
    /// <param name="image">The source image.</param>
    /// <returns>A bitmap the caller must dispose. Its alpha type is Premul, Unpremul or Opaque according to <see cref="PixelImage.Format"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    public static SKBitmap ToSKBitmap(this PixelImage image)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        SKAlphaType alphaType = image.Format switch
        {
            PixelFormat.Bgra32Premultiplied => SKAlphaType.Premul,
            PixelFormat.Bgra32 => SKAlphaType.Unpremul,
            _ => SKAlphaType.Opaque,
        };

        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, alphaType));
        try
        {
            if (image.Data.Length > 0)
            {
                IntPtr dst = bitmap.GetPixels();
                if (bitmap.RowBytes == image.Stride)
                {
                    Marshal.Copy(image.Data, 0, dst, image.Data.Length);
                }
                else
                {
                    for (int y = 0; y < image.Height; y++)
                    {
                        Marshal.Copy(image.Data, y * image.Stride, dst + y * bitmap.RowBytes, image.Stride);
                    }
                }
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        return bitmap;
    }

    /// <summary>Copies the pixels into a new <see cref="SKImage"/>.</summary>
    /// <param name="image">The source image.</param>
    /// <returns>An image the caller must dispose.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    public static SKImage ToSKImage(this PixelImage image)
    {
        using SKBitmap bitmap = image.ToSKBitmap();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>Encodes the image as JPEG or PNG.</summary>
    /// <param name="image">The image to encode.</param>
    /// <param name="format">The output format.</param>
    /// <param name="quality">Quality from 0 to 100; ignored for PNG.</param>
    /// <param name="colorMode">
    /// How to treat the ICC profile of the image; see <see cref="ColorMode"/>. With <see cref="ColorMode.Default"/>,
    /// a profile that cannot be converted is embedded instead, unless it is too large for a JPEG (more than
    /// 255 x 65519 bytes), in which case it is dropped.
    /// </param>
    /// <returns>The encoded bytes. JPEG output is always opaque: transparent pixels are flattened on white.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is outside 0 to 100, or <paramref name="format"/> or <paramref name="colorMode"/> is not a defined value.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="colorMode"/> is <see cref="ColorMode.Embed"/>, <paramref name="format"/> is
    /// <see cref="ImageFormat.Jpeg"/>, and the profile is larger than the 255 x 65519 bytes a JPEG can hold.
    /// </exception>
    /// <exception cref="InvalidOperationException">Skia failed to encode the image.</exception>
    public static byte[] Encode(this PixelImage image, ImageFormat format, int quality = 75, ColorMode colorMode = ColorMode.Default)
        => Encode(image, format, quality, colorMode, SkiaColorTransform.GetCached);

    // The transform factory is injectable so tests can make a transform fail.
    internal static byte[] Encode(PixelImage image, ImageFormat format, int quality, ColorMode colorMode, Func<ReadOnlyMemory<byte>, IColorTransform?> transformFactory)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        if (quality < 0 || quality > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quality), quality, "Quality must be between 0 and 100.");
        }

        if (format != ImageFormat.Jpeg && format != ImageFormat.Png)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Undefined image format.");
        }

        if (colorMode != ColorMode.Default && colorMode != ColorMode.Embed && colorMode != ColorMode.Ignore)
        {
            throw new ArgumentOutOfRangeException(nameof(colorMode), colorMode, "Undefined color mode.");
        }

        PixelImage source = format == ImageFormat.Jpeg ? image.ToOpaque(255, 255, 255) : image;
        byte[]? profileToEmbed = null;

        if (!source.IccProfile.IsEmpty && colorMode != ColorMode.Ignore)
        {
            bool embed = true;
            if (colorMode == ColorMode.Default)
            {
                IColorTransform? transform = transformFactory(source.IccProfile);
                if (transform is SkiaColorTransform.NoOpTransform)
                {
                    // The profile is sRGB already: drop it and leave the pixels alone.
                    source = new PixelImage(source.Width, source.Height, source.Format, source.Data);
                    embed = false;
                }
                else if (transform is not null && TryConvertToSrgb(source, transform) is { } converted)
                {
                    source = converted;
                    embed = false;
                }
                else if (format == ImageFormat.Jpeg && !IccEmbedding.FitsInJpeg(source.IccProfile.Length))
                {
                    // Default never fails because of the profile: one that a JPEG cannot hold is dropped.
                    embed = false;
                }
            }

            if (embed)
            {
                profileToEmbed = source.IccProfile.ToArray();
            }
        }

        byte[] encoded = EncodeCore(source, format, quality);
        if (profileToEmbed is null)
        {
            return encoded;
        }

        return format == ImageFormat.Jpeg
            ? IccEmbedding.EmbedJpeg(encoded, profileToEmbed)
            : IccEmbedding.EmbedPng(encoded, profileToEmbed);
    }

    // Default never fails because of the profile: a transform that throws InvalidOperationException (the codec
    // tier cannot convert this profile after all) leaves the image unconverted, to be embedded instead.
    private static PixelImage? TryConvertToSrgb(PixelImage image, IColorTransform transform)
    {
        try
        {
            return ConvertToSrgb(image, transform);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Converts the color channels to sRGB on a copy and returns an image without a profile.
    private static PixelImage ConvertToSrgb(PixelImage image, IColorTransform transform)
    {
        if (image.Format == PixelFormat.Bgra32Opaque)
        {
            byte[] copy = (byte[])image.Data.Clone();
            transform.Apply(copy, image.Width, image.Height);
            return new PixelImage(image.Width, image.Height, PixelFormat.Bgra32Opaque, copy);
        }

        // Transform the colors of straight-alpha pixels as if they were opaque, then put the alpha back.
        PixelImage straight = image.ToStraightAlpha();
        byte[] colors = (byte[])straight.Data.Clone();
        byte[] alpha = new byte[colors.Length / 4];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = colors[i * 4 + 3];
            colors[i * 4 + 3] = 255;
        }

        transform.Apply(colors, image.Width, image.Height);
        for (int i = 0; i < alpha.Length; i++)
        {
            colors[i * 4 + 3] = alpha[i];
        }

        return new PixelImage(image.Width, image.Height, PixelFormat.Bgra32, colors);
    }

    private static byte[] EncodeCore(PixelImage image, ImageFormat format, int quality)
    {
        using SKImage skImage = image.ToSKImage();
        using SKData? data = skImage.Encode(format == ImageFormat.Jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, quality);
        if (data is null)
        {
            throw new InvalidOperationException("Skia failed to encode the image.");
        }

        return data.ToArray();
    }
}
