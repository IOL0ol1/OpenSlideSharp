using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenSlideSharp.Viewer.Core;

namespace OpenSlideSharp.Viewer.Controls;

/// <summary>
/// The bitmaps built from the tiles of one <see cref="TileCache"/>, on the UI thread. Keep it exactly as long as that
/// cache, and use it on the UI thread only.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Get"/> builds a tile's bitmap the first time the tile is drawn, consuming its pixels (see
/// <see cref="TileImage"/>), so from then on the bitmap is the only copy: dropping it while the tile is still cached
/// would leave the tile with nothing to draw.
/// </para>
/// <para>
/// When the cache drops a tile it disposes the <see cref="TileImage"/>, possibly on a loader thread. The bitmap is
/// disposed on the UI thread by the next <see cref="Sweep"/>, which <c>SlideView</c> runs before each frame, or when
/// the same key gets a new tile; a frame already recorded keeps its own reference to the bitmap's pixels, so this is
/// safe. Bitmap memory therefore follows the cache budget, plus the tiles evicted since the last frame.
/// </para>
/// </remarks>
internal sealed class TileBitmapCache : IDisposable
{
    private static readonly Vector Dpi = new(96, 96);

    private readonly Dictionary<TileKey, Entry> _entries = new();
    private readonly List<TileKey> _dropped = new();

    /// <summary>Gets the number of bitmaps held.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Returns the bitmap of <paramref name="tile"/>, the tile cached under <paramref name="key"/>, building it from
    /// the tile's pixels the first time; null if the tile was disposed before it was ever drawn.
    /// </summary>
    public Bitmap? Get(TileKey key, TileImage tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (_entries.TryGetValue(key, out Entry entry))
        {
            if (ReferenceEquals(entry.Tile, tile))
            {
                return entry.Bitmap;
            }

            // The cache replaced the tile under this key, which disposed the old one.
            entry.Bitmap.Dispose();
            _entries.Remove(key);
        }

        Bitmap? bitmap = tile.ConsumePixels(pixels => Create(tile.Width, tile.Height, pixels));
        if (bitmap is not null)
        {
            _entries.Add(key, new Entry(tile, bitmap));
        }

        return bitmap;
    }

    /// <summary>Disposes the bitmaps of the tiles the cache has dropped since the last sweep.</summary>
    /// <returns>The number of bitmaps disposed.</returns>
    public int Sweep()
    {
        foreach (KeyValuePair<TileKey, Entry> pair in _entries)
        {
            if (pair.Value.Tile.IsDisposed)
            {
                _dropped.Add(pair.Key);
            }
        }

        foreach (TileKey key in _dropped)
        {
            _entries.Remove(key, out Entry entry);
            entry.Bitmap.Dispose();
        }

        int count = _dropped.Count;
        _dropped.Clear();
        return count;
    }

    /// <summary>Disposes every bitmap. Call it together with clearing the cache.</summary>
    public void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Bitmap.Dispose();
        }

        _entries.Clear();
    }

    public void Dispose() => Clear();

    private static Bitmap Create(int width, int height, byte[] pixels)
    {
        // OpenSlide's premultiplied BGRA is Avalonia's Bgra8888 with premultiplied alpha; the bitmap copies it.
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(
                Avalonia.Platform.PixelFormat.Bgra8888,
                AlphaFormat.Premul,
                handle.AddrOfPinnedObject(),
                new PixelSize(width, height),
                Dpi,
                width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private readonly record struct Entry(TileImage Tile, Bitmap Bitmap);
}
