using System;
using System.IO;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class CheckModeTests
{
    [Theory]
    [InlineData("small.svs", "slide=16x16")]
    [InlineData("boxes.tiff", "slide=300x250")]
    public void Check_OpensSlide_PrintsVersionAndSize(string fixture, string size)
    {
        var output = new StringWriter();

        int code = Program.RunCheck(["--check", TestFiles.Get(fixture)], output);

        Assert.True(code == 0, output.ToString());
        Assert.Contains("openslide=4.", output.ToString());
        Assert.Contains(size, output.ToString());
    }

    [Theory]
    [InlineData("unopenable.tiff")]
    [InlineData("missing.svs")]
    public void Check_Unopenable_Returns1(string fixture)
    {
        var output = new StringWriter();

        int code = Program.RunCheck(["--check", TestFiles.Get(fixture)], output);

        Assert.Equal(1, code);
        Assert.Contains(fixture, output.ToString());
        Assert.DoesNotContain(
            output.ToString().Split(Environment.NewLine),
            line => line.StartsWith("slide=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData]
    [InlineData("--check")]
    [InlineData("--check", "a.svs", "b.svs")]
    public void Check_BadArguments_Returns2(params string[] args)
    {
        var output = new StringWriter();

        int code = Program.RunCheck(args, output);

        Assert.Equal(2, code);
        Assert.Contains("--check <slide>", output.ToString());
    }
}
