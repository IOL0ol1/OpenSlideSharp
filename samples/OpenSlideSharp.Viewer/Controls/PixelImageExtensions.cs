using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OpenSlideSharp.Viewer.Controls;

internal static class PixelImageExtensions
{
    /// <summary>
    /// Copies the image into a new bitmap with the same pixels (both are B, G, R, A in memory), or returns null for
    /// an image without pixels. Call on the UI thread.
    /// </summary>
    public static WriteableBitmap? ToBitmap(this PixelImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width == 0 || image.Height == 0)
        {
            return null;
        }

        AlphaFormat alpha = image.Format switch
        {
            OpenSlideSharp.PixelFormat.Bgra32Premultiplied => AlphaFormat.Premul,
            OpenSlideSharp.PixelFormat.Bgra32 => AlphaFormat.Unpremul,
            _ => AlphaFormat.Opaque,
        };
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, alpha);
        using ILockedFramebuffer buffer = bitmap.Lock();
        for (int y = 0; y < image.Height; y++)
        {
            Marshal.Copy(image.Data, y * image.Stride, buffer.Address + (y * buffer.RowBytes), image.Stride);
        }

        return bitmap;
    }
}
