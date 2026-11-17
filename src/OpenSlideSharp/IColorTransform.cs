using System;

namespace OpenSlideSharp;

/// <summary>Converts opaque BGRA pixels from an image's own color space to sRGB.</summary>
public interface IColorTransform
{
    /// <summary>Converts <paramref name="pixels"/> in place; afterwards they are treated as sRGB.</summary>
    /// <remarks>Implementations must be safe to call from several threads at the same time.</remarks>
    /// <param name="pixels">Opaque B, G, R, A pixels, rows top to bottom with a stride of <c>width * 4</c>.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    void Apply(Span<byte> pixels, int width, int height);
}
