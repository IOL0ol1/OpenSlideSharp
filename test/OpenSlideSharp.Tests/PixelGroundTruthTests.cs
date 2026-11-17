using Xunit;

namespace OpenSlideSharp.Tests;

/// <summary>Pins the <see cref="PixelFormat.Bgra32Premultiplied"/> contract (bytes B, G, R, A) with real slide data.</summary>
public class PixelGroundTruthTests
{
    private const int Width = 300; // boxes.tiff level 0
    private const int Height = 250;

    [Fact]
    public void RegionOutsideSlide_IsTransparentBlack()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var img = osr.ReadRegion(-1000, -1000, 0, 16, 16);
        Assert.Equal(16 * 16 * 4, img.Data.Length);
        Assert.All(img.Data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Level0_IsOpaque_AndColorsDoNotExceedAlpha()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(new SlideSize(Width, Height), osr.Dimensions);
        byte[] data = osr.ReadRegion(0, 0, 0, Width, Height).Data;
        for (int i = 0; i < data.Length; i += 4)
        {
            byte a = data[i + 3];
            if (a != 255 || data[i] > a || data[i + 1] > a || data[i + 2] > a)
            {
                int pixel = i / 4;
                Assert.Fail($"pixel ({pixel % Width}, {pixel / Width}) has BGRA {data[i]}, {data[i + 1]}, {data[i + 2]}, {a}");
            }
        }
    }

    // Reference values from openslide-python 1.4.7 with openslide-bin 4.0.1.2 (OpenSlide 4.0.1):
    //   openslide.OpenSlide("boxes.tiff").read_region((x, y), 0, (1, 1)).getpixel((0, 0))
    // Python returns straight RGBA; with A == 255 premultiplied equals straight, so the bytes are B, G, R, A.
    [Theory]
    [InlineData(40, 40, 0, 0, 255, 255)]     // red box:   RGBA (255, 0, 0, 255)
    [InlineData(100, 100, 255, 0, 0, 255)]   // blue box:  RGBA (0, 0, 255, 255)
    [InlineData(220, 110, 0, 255, 0, 255)]   // green box: RGBA (0, 255, 0, 255)
    [InlineData(10, 10, 255, 255, 255, 255)] // white background
    public void Level0_MatchesOpenSlidePython(int x, int y, byte b, byte g, byte r, byte a)
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        byte[] expected = { b, g, r, a };
        Assert.Equal(expected, osr.ReadRegion(x, y, 0, 1, 1).Data);

        // The same pixel inside a whole-level read, so row and column addressing is pinned too.
        byte[] level = osr.ReadRegion(0, 0, 0, Width, Height).Data;
        int offset = ((y * Width) + x) * 4;
        Assert.Equal(expected, level[offset..(offset + 4)]);
    }
}
