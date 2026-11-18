namespace OpenSlideSharp.Viewer.Core;

/// <summary>A tile of the <see cref="TileGrid"/>: its level, and its column and row in that level's 256-pixel grid.</summary>
internal readonly record struct TileKey(int Level, long Column, long Row);
