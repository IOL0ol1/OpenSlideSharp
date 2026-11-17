using System;
using Xunit;

namespace OpenSlideSharp.Tests;

public class ErrorStateTests
{
    [Fact]
    public void ReadBadRegion_IsSticky()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "OpenSlide's libjpeg longjmp error path crashes .NET processes on Windows (known issue: see docs/known-issues/windows-cfg-longjmp-crash.md); covered on Linux/macOS CI");
        using var osr = OpenSlide.Open(TestFiles.Get("unreadable.svs"));
        Assert.Throws<OpenSlideException>(() => osr.ReadRegion(0, 0, 0, 100, 100));
        Assert.Throws<OpenSlideException>(() => osr.LevelCount);
        Assert.Throws<OpenSlideException>(() => osr.Properties["openslide.vendor"]);
    }

    [Fact]
    public void Dispose_AfterStickyError_DoesNotThrow()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        StickyError.PutInErrorState(osr);
        Assert.Throws<OpenSlideException>(() => osr.LevelCount);
        Assert.Throws<OpenSlideException>(() => osr.Properties["openslide.vendor"]);
        osr.Dispose();
        osr.Dispose();
    }
}
