using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
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
    public void ColorProfile_Boxes_And_Svs()
    {
        using var boxes = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(588, boxes.ColorProfile!.Length);
        Assert.Equal("mntr", Encoding.ASCII.GetString(boxes.ColorProfile, 12, 4));
        Assert.Same(boxes.ColorProfile, boxes.ColorProfile);
        Assert.Equal(588, boxes.ReadRegion(0, 0, 0, 100, 100).IccProfile!.Length);
        using var svs = OpenSlide.Open(TestFiles.Get("small.svs"));
        Assert.Null(svs.ColorProfile);
        Assert.Null(svs.ReadRegion(0, 0, 0, 100, 100).IccProfile);
    }

    [Fact]
    public async Task ReadRegion_ConcurrentWithDispose_OnlyObjectDisposed()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var errors = new ConcurrentBag<Exception>();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                try { osr.ReadRegion(0, 0, 0, 300, 250); }
                catch (Exception e) { errors.Add(e); }
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.Delay(5, TestContext.Current.CancellationToken);
        osr.Dispose();
        await Task.WhenAll(readers);
        Assert.All(errors, e => Assert.IsType<ObjectDisposedException>(e));
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
