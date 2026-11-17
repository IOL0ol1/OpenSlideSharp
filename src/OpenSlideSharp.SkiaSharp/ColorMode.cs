namespace OpenSlideSharp.SkiaSharp;

/// <summary>What to do with the ICC color profile of an image when encoding it.</summary>
public enum ColorMode
{
    /// <summary>
    /// Convert the pixels to sRGB and write no profile; if the profile cannot be converted, behave like
    /// <see cref="Embed"/>, except that a profile too large for a JPEG is dropped rather than failing.
    /// </summary>
    Default,

    /// <summary>Keep the pixels as they are and embed the ICC profile in the output.</summary>
    Embed,

    /// <summary>Keep the pixels as they are and drop the ICC profile.</summary>
    Ignore,
}
