using Xunit;

namespace OpenSlideSharp.Tests;

public class CompositorTests
{
    [Theory]
    [InlineData(64, 32, 16, 128, 255, 255, 255, 191, 159, 143, 255)]
    [InlineData(64, 32, 16, 128, 0, 0, 0, 64, 32, 16, 255)]
    [InlineData(64, 32, 16, 128, 0x12, 0x34, 0x56, 107, 58, 25, 255)]
    [InlineData(0, 0, 0, 0, 0x12, 0x34, 0x56, 86, 52, 18, 255)]
    [InlineData(10, 20, 30, 255, 0x12, 0x34, 0x56, 10, 20, 30, 255)]
    [InlineData(1, 2, 3, 7, 255, 255, 255, 249, 250, 251, 255)]
    [InlineData(200, 100, 50, 200, 255, 255, 255, 255, 155, 104, 255)]
    [InlineData(200, 100, 50, 200, 0x12, 0x34, 0x56, 219, 111, 53, 255)]
    public void CompositeInPlace_MatchesPillow(
        byte b, byte g, byte r, byte a, byte bgR, byte bgG, byte bgB, byte eb, byte eg, byte er, byte ea)
    {
        byte[] px = { b, g, r, a };
        Compositor.CompositeInPlace(px, bgR, bgG, bgB);
        Assert.Equal(new[] { eb, eg, er, ea }, px);
    }

    [Fact]
    public void TryParseBackground_AcceptsHexCaseInsensitive()
    {
        Assert.True(Compositor.TryParseBackground("ffFF00", out byte r, out byte g, out byte b));
        Assert.Equal((255, 255, 0), (r, g, b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zzzzzz")]
    [InlineData("fff")]
    [InlineData("#ffffff")]
    [InlineData("fffffff")]
    public void TryParseBackground_RejectsMalformed(string? hex)
    {
        Assert.False(Compositor.TryParseBackground(hex, out byte r, out byte g, out byte b));
        Assert.Equal((255, 255, 255), (r, g, b));
    }
}
