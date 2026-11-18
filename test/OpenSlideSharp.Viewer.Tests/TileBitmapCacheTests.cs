using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using OpenSlideSharp.Viewer.Controls;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class TileBitmapCacheTests
{
    private const int TileBytes = 2 * 2 * 4;

    private static TileKey Key(int i) => new(0, i, 0);

    private static TileImage Tile(List<byte[]>? released = null) =>
        new(2, 2, new byte[TileBytes], released is null ? null : released.Add);

    private static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [Fact]
    public void BitmapCache_DisposesTheBitmapsOfEvictedTiles()
    {
        HeadlessSession.Run(() =>
        {
            using var cache = new TileCache(2 * TileBytes);
            using var bitmaps = new TileBitmapCache();
            TileImage a = Tile(), b = Tile(), c = Tile();
            cache.Add(Key(0), a);
            cache.Add(Key(1), b);
            Bitmap bitmapA = bitmaps.Get(Key(0), a)!;
            Bitmap bitmapB = bitmaps.Get(Key(1), b)!;
            Assert.Same(bitmapA, bitmaps.Get(Key(0), a)); // built once
            Assert.Equal(2, bitmaps.Count);

            cache.Add(Key(2), c); // evicts a, the least recently used
            Assert.True(a.IsDisposed);
            Assert.False(IsDisposed(bitmapA)); // until the next sweep: a frame being drawn may still use it

            Assert.Equal(1, bitmaps.Sweep());
            Assert.True(IsDisposed(bitmapA));
            Assert.False(IsDisposed(bitmapB));
            Assert.Equal(1, bitmaps.Count);
            Assert.Equal(0, bitmaps.Sweep());

            cache.Clear();
            Assert.Equal(1, bitmaps.Sweep());
            Assert.True(IsDisposed(bitmapB));
            Assert.Equal(0, bitmaps.Count);
        });
    }

    [Fact]
    public void BitmapCache_ReplacedTile_DisposesTheOldBitmap()
    {
        HeadlessSession.Run(() =>
        {
            using var cache = new TileCache(1 << 20);
            using var bitmaps = new TileBitmapCache();
            TileImage first = Tile(), second = Tile();
            cache.Add(Key(0), first);
            Bitmap old = bitmaps.Get(Key(0), first)!;

            cache.Add(Key(0), second); // the same tile read again
            Bitmap replacement = bitmaps.Get(Key(0), second)!;

            Assert.NotSame(old, replacement);
            Assert.True(IsDisposed(old));
            Assert.Equal(1, bitmaps.Count);
        });
    }

    [Fact]
    public void BitmapCache_BuildsTheBitmapFromThePixelsThenReleasesThem()
    {
        HeadlessSession.Run(() =>
        {
            using var bitmaps = new TileBitmapCache();
            var released = new List<byte[]>();
            // Premultiplied BGRA: opaque blue, half-transparent red, transparent, opaque white.
            byte[] pixels = [255, 0, 0, 255, 0, 0, 128, 128, 0, 0, 0, 0, 255, 255, 255, 255, 99, 99]; // two spare bytes
            var tile = new TileImage(2, 2, pixels, released.Add);

            Bitmap bitmap = bitmaps.Get(Key(0), tile)!;

            Assert.Equal(new PixelSize(2, 2), bitmap.PixelSize);
            Assert.Equal(Avalonia.Platform.PixelFormat.Bgra8888, bitmap.Format);
            Assert.Equal(Avalonia.Platform.AlphaFormat.Premul, bitmap.AlphaFormat);
            var copy = new byte[TileBytes];
            GCHandle handle = GCHandle.Alloc(copy, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, 2, 2), handle.AddrOfPinnedObject(), copy.Length, 2 * 4);
            }
            finally
            {
                handle.Free();
            }

            Assert.Equal(pixels.AsSpan(0, TileBytes).ToArray(), copy);
            // The bitmap holds the pixels now: the buffer went back to its owner straight away.
            Assert.Same(pixels, Assert.Single(released));
            Assert.False(tile.HasPixels);
            Assert.Same(bitmap, bitmaps.Get(Key(0), tile));
        });
    }

    [Fact]
    public void BitmapCache_DisposedTile_HasNoBitmap()
    {
        HeadlessSession.Run(() =>
        {
            using var bitmaps = new TileBitmapCache();
            var released = new List<byte[]>();
            TileImage tile = Tile(released);
            tile.Dispose(); // evicted before it was ever drawn

            Assert.Null(bitmaps.Get(Key(0), tile));
            Assert.Equal(0, bitmaps.Count);
            Assert.Single(released);
        });
    }

    [Fact]
    public void BitmapCache_Dispose_DisposesEveryBitmap()
    {
        HeadlessSession.Run(() =>
        {
            var bitmaps = new TileBitmapCache();
            Bitmap bitmap = bitmaps.Get(Key(0), Tile())!;

            bitmaps.Dispose();

            Assert.True(IsDisposed(bitmap));
            Assert.Equal(0, bitmaps.Count);
        });
    }
}
