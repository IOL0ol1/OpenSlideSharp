using System;
using System.Linq;
using Xunit;

namespace OpenSlideSharp.Tests.Rendering;

public class RendererTests
{
    [Fact]
    public void Render_RegionTooLarge_ThrowsBeforeReading()
    {
        var source = FakeRenderSource.SingleLevel(100000, 100000);
        var geometry = new SlideGeometry(source, limitBounds: false);
        Assert.Equal(18, geometry.LevelCount); // scale exponent 17 is the 1x1 Deep Zoom level

        var renderer = new RegionRenderer(source, geometry, colorTransform: null);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Render(17, 0, 0, 1, 1, new byte[4]));

        Assert.Contains("100000 x 100000", ex.Message);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public void Render_EmptyRegion_FillsBackground()
    {
        var source = FakeRenderSource.SingleLevel(
            10, 10, (OpenSlideProperties.BoundsWidth, "0"), (OpenSlideProperties.BackgroundColor, "112233"));
        var geometry = new SlideGeometry(source, limitBounds: true);
        Assert.Equal(new SlideSize(0, 10), geometry.ActiveSize);

        byte[] destination = Enumerable.Repeat((byte)0x5A, 16 * 4).ToArray();
        new RegionRenderer(source, geometry, colorTransform: null).Render(0, 0, 0, 4, 4, destination);

        for (int i = 0; i < 16; i++)
            Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 255 }, destination.AsSpan(i * 4, 4).ToArray());
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public void AssociatedImageSource_OutsideIsTransparent()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("small.svs"));
        AssociatedImage image = osr.AssociatedImages["thumbnail"];
        var source = new AssociatedImageRenderSource(image);

        Assert.Equal(new[] { new SlideSize(16, 16) }, source.LevelDimensions);
        Assert.Equal(new[] { 1.0 }, source.LevelDownsamples);
        Assert.Equal(0, source.GetBestLevelForDownsample(64));
        Assert.Null(source.GetProperty(OpenSlideProperties.Vendor));
        Assert.True(source.ColorProfile.IsEmpty);
        Assert.Equal("AssociatedImage(thumbnail)", source.Describe());

        // Pre-fill with garbage: everything outside the image must be overwritten with transparent zeros.
        byte[] destination = Enumerable.Repeat((byte)0xAB, 4 * 4 * 4).ToArray();
        source.ReadRegion(-2, -2, 0, 4, 4, destination);

        byte[] full = image.Read().Data;
        for (int ty = 0; ty < 4; ty++)
        {
            for (int tx = 0; tx < 4; tx++)
            {
                byte[] actual = destination.AsSpan((ty * 4 + tx) * 4, 4).ToArray();
                if (tx >= 2 && ty >= 2)
                {
                    byte[] expected = full.AsSpan(((ty - 2) * 16 + (tx - 2)) * 4, 4).ToArray();
                    Assert.Equal(expected, actual);
                    Assert.NotEqual(0, actual[3]); // the image itself is opaque, so the copy is meaningful
                }
                else
                {
                    Assert.Equal(new byte[4], actual);
                }
            }
        }
    }

    [Fact]
    public void Create_ChecksArgumentThenUsability()
    {
        Assert.Throws<ArgumentNullException>("slide", () => SlideRenderSource.Create(null!));
        Assert.Throws<ArgumentNullException>("image", () => AssociatedImageRenderSource.Create(null!));

        var osr = OpenSlide.Open(TestFiles.Get("small.svs"));
        AssociatedImage thumbnail = osr.AssociatedImages["thumbnail"];
        Assert.Equal("OpenSlide(" + osr.FileName + ")", SlideRenderSource.Create(osr).Describe());
        Assert.Equal("AssociatedImage(thumbnail)", AssociatedImageRenderSource.Create(thumbnail).Describe());

        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => SlideRenderSource.Create(osr));
        Assert.Throws<ObjectDisposedException>(() => AssociatedImageRenderSource.Create(thumbnail));
    }
}
