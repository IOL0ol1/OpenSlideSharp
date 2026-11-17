using System;
using System.IO;
using Xunit;

namespace OpenSlideSharp.Tests;

public class OpenSlideTests
{
    [Fact] public void DetectFormat_Missing_Null() => Assert.Null(OpenSlide.DetectFormat(TestFiles.Get("__missing_file")));
    [Fact] public void DetectFormat_NotASlide_Null() => Assert.Null(OpenSlide.DetectFormat(TestFiles.Get("README.md")));
    [Fact] public void DetectFormat_Boxes() => Assert.Equal("generic-tiff", OpenSlide.DetectFormat(TestFiles.Get("boxes.tiff")));

    [Fact]
    public void Open_Unsupported_Throws()
    {
        Assert.Throws<OpenSlideUnsupportedFormatException>(() => OpenSlide.Open("__does_not_exist"));
        Assert.Throws<OpenSlideUnsupportedFormatException>(() => OpenSlide.Open(TestFiles.Get("README.md")));
        // The real fixture is recognized (generic-tiff) but broken, so OpenSlide returns a handle in the error state:
        // exactly OpenSlideException, as in openslide-python's _check_open. (openslide-python's test_open passes the
        // bare relative path 'unopenable.tiff', which does not exist, and so gets the unsupported-format error.)
        Assert.Throws<OpenSlideException>(() => OpenSlide.Open(TestFiles.Get("unopenable.tiff")));
        Assert.Throws<ArgumentNullException>(() => OpenSlide.Open(null!));
    }

    [Fact] // Native code would stop at the NUL and silently open "small.svs".
    public void Open_And_DetectFormat_EmbeddedNul_Throw()
    {
        string path = TestFiles.Get("small.svs") + "\0x";
        Assert.Equal("filename", Assert.Throws<ArgumentException>(() => OpenSlide.Open(path)).ParamName);
        Assert.Equal("filename", Assert.Throws<ArgumentException>(() => OpenSlide.DetectFormat(path)).ParamName);
    }

    [Fact]
    public void Open_UnicodePath()
    {
        string path = TestFiles.Get("😐.svs");
        Assert.Equal("aperio", OpenSlide.DetectFormat(path));
        using var osr = OpenSlide.Open(path);
        Assert.Equal(new SlideSize(16, 16), osr.Dimensions);
    }

    [Fact] // Review Focus 2
    public void Open_ChineseDirectoryAndFileName_Works()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openslide 测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "切片.svs");
            File.Copy(TestFiles.Get("small.svs"), path);
            Assert.Equal("aperio", OpenSlide.DetectFormat(path));
            using var osr = OpenSlide.Open(path);
            using var reference = OpenSlide.Open(TestFiles.Get("small.svs"));
            Assert.Equal(reference.Dimensions, osr.Dimensions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BasicMetadata_Boxes()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(4, osr.LevelCount);
        Assert.Equal(new[] { new SlideSize(300, 250), new SlideSize(150, 125), new SlideSize(75, 62), new SlideSize(37, 31) }, osr.LevelDimensions);
        Assert.Equal(new SlideSize(300, 250), osr.Dimensions);
        Assert.Equal(4, osr.LevelDownsamples.Count);
        Assert.Equal(1.0, osr.LevelDownsamples[0]);
        Assert.Equal(2.0, osr.LevelDownsamples[1]);
        Assert.Equal(4.0, osr.LevelDownsamples[2], 0);
        Assert.Equal(8.0, osr.LevelDownsamples[3], 0);
        Assert.Equal(0, osr.GetBestLevelForDownsample(0.5));
        Assert.Equal(1, osr.GetBestLevelForDownsample(3));
        Assert.Equal(3, osr.GetBestLevelForDownsample(37));
        Assert.Equal($"OpenSlide({TestFiles.Get("boxes.tiff")})", osr.ToString());
    }

    [Fact]
    public void AfterDispose_MembersThrow_DisposeIdempotent()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(4, osr.LevelCount);
        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => osr.LevelCount);
        osr.Dispose();
    }
}
