namespace OpenSlideSharp;

/// <summary>The size, in pixels, of a slide level or an associated image.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct SlideSize(long Width, long Height);
