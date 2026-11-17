using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace OpenSlideSharp.Tests;

public class AssociatedImageTests
{
    [Fact]
    public void Aperio_Thumbnail()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("small.svs"));
        var thumb = osr.AssociatedImages["thumbnail"];
        Assert.Equal(new SlideSize(16, 16), thumb.Dimensions);
        var img = thumb.Read();
        Assert.Equal((16, 16), (img.Width, img.Height));
        Assert.True(thumb.ColorProfile.IsEmpty);
        Assert.True(img.IccProfile.IsEmpty);
        Assert.Equal("AssociatedImage(thumbnail)", thumb.ToString());
        Assert.Throws<KeyNotFoundException>(() => osr.AssociatedImages["__missing"]);
        Assert.Equal(osr.AssociatedImages.Count, osr.AssociatedImages.Count());
    }

    [Fact]
    public void Dicom_SharedColorProfile()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes_0.dcm"));
        Assert.Equal(456, osr.ColorProfile.Length);
        Assert.Equal("mntr", Encoding.ASCII.GetString(osr.ColorProfile.Span.Slice(12, 4).ToArray()));
        Assert.Equal(456, osr.ReadRegion(0, 0, 0, 100, 100).IccProfile.Length);
        Assert.Equal(osr.ColorProfile.ToArray(), osr.AssociatedImages["thumbnail"].ColorProfile.ToArray());
    }

    [Fact]
    public void UnreadableAssociated_IsSticky()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "OpenSlide's libjpeg longjmp error path crashes .NET processes on Windows (known issue: see docs/known-issues/windows-cfg-longjmp-crash.md); covered on Linux/macOS CI");
        using var osr = OpenSlide.Open(TestFiles.Get("unreadable.svs"));
        Assert.Equal("aperio", osr.Properties[OpenSlideProperties.Vendor]);
        Assert.Throws<OpenSlideException>(() => osr.AssociatedImages["thumbnail"].Read());
        Assert.Throws<OpenSlideException>(() => osr.Properties[OpenSlideProperties.Vendor]);
    }

    [Fact]
    public void AssociatedImages_AfterDispose_Throw()
    {
        var osr = OpenSlide.Open(TestFiles.Get("small.svs"));
        var map = osr.AssociatedImages;
        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => map["thumbnail"]);
        Assert.Throws<ObjectDisposedException>(() => map.Count);
        Assert.Throws<ObjectDisposedException>(() => map.Keys.ToList());
        Assert.Throws<ObjectDisposedException>(() => map.ContainsKey("thumbnail"));
    }
}
