using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenSlideSharp.Tests;

public class ReadRegionTests
{
    [Fact]
    public void ReadRegion_Sizes()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var img = osr.ReadRegion(-10, -10, 1, 400, 400);
        Assert.Equal((400, 400), (img.Width, img.Height));
        Assert.Equal(400 * 400 * 4, img.Data.Length);
        Assert.Equal(PixelFormat.Bgra32Premultiplied, img.Format);
        var empty = osr.ReadRegion(0, 0, 1, 400, 0);
        Assert.Equal((400, 0), (empty.Width, empty.Height));
        Assert.Equal(100, osr.ReadRegion(0, 0, 4, 100, 100).Width); // out-of-range level does not throw, like Python
        Assert.Throws<ArgumentOutOfRangeException>(() => osr.ReadRegion(0, 0, 1, 400, -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => osr.ReadRegion(0, 0, 1, -5, 400));
    }

    [Fact]
    public void ReadRegion_TooLargeForArray_Throws()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Throws<ArgumentOutOfRangeException>(() => osr.ReadRegion(0, 0, 0, 65536, 65536));
    }

    [Fact]
    public void ReadRegion_SpanTooSmall_Throws()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Throws<ArgumentException>(() => osr.ReadRegion(0, 0, 0, 10, 10, new byte[10 * 10 * 4 - 1]));
    }

    [Fact]
    public void ReadRegion_SpanOverload_MatchesArrayOverload()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var buffer = new byte[50 * 40 * 4];
        osr.ReadRegion(0, 0, 0, 50, 40, buffer);
        Assert.Equal(osr.ReadRegion(0, 0, 0, 50, 40).Data, buffer);
    }

    [Fact]
    public void ToOpaque_PremultipliedOverWhite()
    {
        byte[] icc = { 1, 2, 3 };
        var img = new PixelImage(1, 1, PixelFormat.Bgra32Premultiplied, new byte[] { 64, 32, 16, 128 }, icc);
        var opaque = img.ToOpaque(255, 255, 255);
        Assert.Equal(PixelFormat.Bgra32Opaque, opaque.Format);
        Assert.Equal(new byte[] { 191, 159, 143, 255 }, opaque.Data);
        Assert.Equal(icc, opaque.IccProfile.ToArray());
        Assert.Equal(new byte[] { 64, 32, 16, 128 }, img.Data);
    }

    [Fact]
    public void ToOpaque_StraightAlphaOverWhite()
    {
        byte[] icc = { 4, 5, 6 };
        var img = new PixelImage(1, 1, PixelFormat.Bgra32, new byte[] { 127, 63, 31, 128 }, icc);
        var opaque = img.ToOpaque(255, 255, 255);
        Assert.Equal(PixelFormat.Bgra32Opaque, opaque.Format);
        Assert.Equal(new byte[] { 191, 159, 143, 255 }, opaque.Data);
        Assert.Equal(icc, opaque.IccProfile.ToArray());
    }

    [Fact]
    public void ToOpaque_OpaqueReturnsSameInstance()
    {
        var img = new PixelImage(1, 1, PixelFormat.Bgra32Opaque, new byte[] { 1, 2, 3, 255 });
        Assert.Same(img, img.ToOpaque(0, 0, 0));
    }

    [Fact]
    public void ColorProfile_Boxes_And_Svs()
    {
        using var boxes = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(588, boxes.ColorProfile.Length);
        Assert.Equal("mntr", Encoding.ASCII.GetString(boxes.ColorProfile.Span.Slice(12, 4).ToArray()));
        Assert.True(boxes.ColorProfile.Equals(boxes.ColorProfile));
        Assert.Equal(588, boxes.ReadRegion(0, 0, 0, 100, 100).IccProfile.Length);
        using var svs = OpenSlide.Open(TestFiles.Get("small.svs"));
        Assert.True(svs.ColorProfile.IsEmpty);
        Assert.True(svs.ReadRegion(0, 0, 0, 100, 100).IccProfile.IsEmpty);
    }

    [Fact]
    public async Task ReadRegion_ConcurrentWithDispose_OnlyObjectDisposed()
    {
        const int readerCount = 8;
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        try
        {
            using var firstReadDone = new CountdownEvent(readerCount);
            int successes = 0;
            var caught = new ConcurrentBag<Exception>();

            // Each reader reads until an exception ends it, so the loop only ends once Dispose has won.
            // Dedicated threads keep thread-pool start-up from delaying any reader.
            var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Factory.StartNew(() =>
            {
                bool signalled = false;
                try
                {
                    while (true)
                    {
                        osr.ReadRegion(0, 0, 0, 300, 250);
                        Interlocked.Increment(ref successes);
                        if (!signalled)
                        {
                            signalled = true;
                            firstReadDone.Signal();
                        }
                    }
                }
                catch (Exception e)
                {
                    caught.Add(e);
                    if (!signalled)
                    {
                        firstReadDone.Signal();
                    }
                }
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            // Dispose only after every reader has completed a read, so reads are guaranteed to be in flight.
            Assert.True(firstReadDone.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                "readers did not start in time");
            osr.Dispose();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.True(successes >= readerCount, "every reader should have read successfully before Dispose");
            Assert.Equal(readerCount, caught.Count); // each reader ended with exactly one exception
            Assert.All(caught, e => Assert.IsType<ObjectDisposedException>(e));
        }
        finally
        {
            osr.Dispose();
        }
    }
}

public class PixelImageTests
{
    [Fact]
    public void ToStraightAlpha_MatchesPythonConvert()
    {
        var src = new PixelImage(3, 1, PixelFormat.Bgra32Premultiplied,
            new byte[] { 16, 32, 64, 128, 10, 20, 30, 255, 5, 6, 7, 0 });
        var dst = src.ToStraightAlpha();
        Assert.Equal(PixelFormat.Bgra32, dst.Format);
        Assert.Equal(new byte[] { 31, 63, 127, 128, 10, 20, 30, 255, 0, 0, 0, 0 }, dst.Data);
        Assert.Equal(new byte[] { 16, 32, 64, 128, 10, 20, 30, 255, 5, 6, 7, 0 }, src.Data); // source untouched
    }

    [Fact]
    public void ToStraightAlpha_OtherFormats_ReturnSameInstance()
    {
        var img = new PixelImage(1, 1, PixelFormat.Bgra32, new byte[4]);
        Assert.Same(img, img.ToStraightAlpha());
    }

    [Fact]
    public void Constructor_RejectsWrongLength() =>
        Assert.Throws<ArgumentException>(() => new PixelImage(2, 2, PixelFormat.Bgra32, new byte[15]));

    [Fact]
    public void Stride_IsWidthTimesFour() =>
        Assert.Equal(8, new PixelImage(2, 1, PixelFormat.Bgra32, new byte[8]).Stride);
}

public class CacheTests
{
    [Fact]
    public void CreateCache()
    {
        new OpenSlideCache(0).Dispose();
        new OpenSlideCache(1).Dispose();
        new OpenSlideCache(4 << 20).Dispose();
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenSlideCache(-1));
    }

    [Fact]
    public void SetCache_ThenRead()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        using (var cache = new OpenSlideCache(64 << 10)) osr.SetCache(cache);
        Assert.Equal(400, osr.ReadRegion(0, 0, 0, 400, 400).Width);
        Assert.Throws<ArgumentNullException>(() => osr.SetCache(null!));
    }

    [Fact]
    public void SetCache_DisposedCache_Throws()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var cache = new OpenSlideCache(1024);
        cache.Dispose();
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => osr.SetCache(cache));
    }
}
