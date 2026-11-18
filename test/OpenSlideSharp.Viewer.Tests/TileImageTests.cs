using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class TileImageTests
{
    [Fact]
    public void TileImage_ConsumePixels_HandsThemOverOnceThenReleasesThem()
    {
        var released = new List<byte[]>();
        byte[] pixels = new byte[(2 * 2 * 4) + 16]; // a pooled array can be longer than the tile needs
        var tile = new TileImage(2, 2, pixels, released.Add);
        Assert.Equal(16, tile.SizeBytes);
        Assert.True(tile.HasPixels);

        string? built = tile.ConsumePixels(p =>
        {
            Assert.Same(pixels, p);
            Assert.Empty(released); // still the consumer's while it runs
            return "bitmap";
        });

        Assert.Equal("bitmap", built);
        Assert.Same(pixels, Assert.Single(released));
        Assert.False(tile.HasPixels);
        Assert.Equal(16, tile.SizeBytes); // still what it counts for in the cache
        Assert.Null(tile.ConsumePixels(_ => "again"));
        tile.Dispose();
        Assert.Single(released); // never released twice
        Assert.True(tile.IsDisposed);
    }

    [Fact]
    public void TileImage_Dispose_ReleasesPixelsNotConsumed()
    {
        var released = new List<byte[]>();
        byte[] pixels = new byte[4 * 4 * 4];
        var tile = new TileImage(4, 4, pixels, released.Add);

        tile.Dispose();
        tile.Dispose();

        Assert.Same(pixels, Assert.Single(released));
        Assert.False(tile.HasPixels);
        Assert.Null(tile.ConsumePixels(_ => "too late"));
    }

    [Fact]
    public async Task TileImage_ConsumeRacingDispose_ReleasesEachBufferOnce()
    {
        // The UI thread consumes a tile while a loader thread evicts it: exactly one of them gets the buffer.
        var released = new ConcurrentQueue<byte[]>();
        var consumed = 0;
        const int Tiles = 2000;
        var tiles = new TileImage[Tiles];
        for (int i = 0; i < Tiles; i++)
        {
            tiles[i] = new TileImage(1, 1, new byte[4], released.Enqueue);
        }

        using var start = new Barrier(2);
        Task consume = Task.Run(() =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            foreach (TileImage tile in tiles)
            {
                if (tile.ConsumePixels(_ => "bitmap") is not null)
                {
                    Interlocked.Increment(ref consumed);
                }
            }
        }, TestContext.Current.CancellationToken);
        Task dispose = Task.Run(() =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            foreach (TileImage tile in tiles)
            {
                tile.Dispose();
            }
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(consume, dispose).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(Tiles, released.Count);
        Assert.Equal(Tiles, new HashSet<byte[]>(released, ReferenceEqualityComparer.Instance).Count);
        Assert.All(tiles, tile => Assert.False(tile.HasPixels));
        Assert.InRange(consumed, 0, Tiles);
    }

    [Fact]
    public void TileImage_RejectsAShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => new TileImage(2, 2, new byte[15]));
        Assert.Throws<ArgumentNullException>(() => new TileImage(2, 2, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileImage(-1, 2, new byte[16]));
    }
}
