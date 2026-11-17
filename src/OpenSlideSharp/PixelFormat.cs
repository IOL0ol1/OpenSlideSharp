namespace OpenSlideSharp;

/// <summary>Describes how the four bytes of each pixel in a <see cref="PixelImage"/> are laid out and interpreted.</summary>
/// <remarks>
/// Every format stores 4 bytes per pixel in memory order B, G, R, A (blue first). On a little-endian machine,
/// <see cref="Bgra32Premultiplied"/> is exactly OpenSlide premultiplied ARGB <see cref="uint"/> pixel.
/// </remarks>
public enum PixelFormat
{
    /// <summary>Bytes B, G, R, A with the color channels premultiplied by alpha. This is what OpenSlide produces.</summary>
    Bgra32Premultiplied,

    /// <summary>Bytes B, G, R, A with straight (non-premultiplied) alpha.</summary>
    Bgra32,

    /// <summary>Bytes B, G, R, A where alpha is always 255, so premultiplied and straight alpha are identical.</summary>
    Bgra32Opaque,
}
