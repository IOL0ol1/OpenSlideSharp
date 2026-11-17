namespace OpenSlideSharp;

/// <summary>The arguments of the <c>OpenSlide.ReadRegion</c> call that covers a Deep Zoom tile.</summary>
/// <param name="X">Level 0 X of the top-left corner.</param>
/// <param name="Y">Level 0 Y of the top-left corner.</param>
/// <param name="Level">The slide level to read from.</param>
/// <param name="Width">Width in pixels at <paramref name="Level"/>.</param>
/// <param name="Height">Height in pixels at <paramref name="Level"/>.</param>
public readonly record struct TileRegion(long X, long Y, int Level, long Width, long Height);
