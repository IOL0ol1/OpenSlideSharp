namespace OpenSlideSharp;

/// <summary>The number of tiles in a Deep Zoom level.</summary>
/// <param name="Columns">Number of tile columns.</param>
/// <param name="Rows">Number of tile rows.</param>
public readonly record struct TileGridSize(long Columns, long Rows);
