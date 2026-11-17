namespace OpenSlideSharp;

/// <summary>Integer arithmetic for tile and chunk grids.</summary>
internal static class GridMath
{
    /// <summary>
    /// Returns how many cells of size <paramref name="divisor"/> cover <paramref name="value"/> pixels:
    /// <c>ceil(value / divisor)</c> for a non-negative <paramref name="value"/> and a positive <paramref name="divisor"/>.
    /// </summary>
    internal static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;
}
