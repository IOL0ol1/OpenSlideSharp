using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OpenSlideSharp.Tests.Rendering;

public class GeometryTests
{
    public static IEnumerable<object[]> FakeCases() =>
        DeepZoomOracle.Load().Geometry
            .Where(g => g.Kind == "fake")
            .Select(g => new object[] { g.Source, g.TileSize, g.Overlap, g.LimitBounds });

    [Theory]
    [MemberData(nameof(FakeCases))]
    public void Geometry_MatchesPython_ForFakes(string source, int tileSize, int overlap, bool limitBounds)
    {
        GeometryOracle entry = DeepZoomOracle.Load().Geometry.Single(g =>
            g.Kind == "fake" && g.Source == source && g.TileSize == tileSize && g.Overlap == overlap &&
            g.LimitBounds == limitBounds);
        var geometry = new SlideGeometry(FakeRenderSource.FromOracle(entry), limitBounds);

        Assert.Equal(entry.LevelCount, geometry.LevelCount);
        Assert.Equal(entry.LevelCount, entry.Levels.Count);
        for (int k = 0; k < entry.LevelCount; k++)
        {
            // Deep Zoom level k has downsample 2^(levelCount - 1 - k).
            int scaleExponent = entry.LevelCount - 1 - k;
            LevelOracle level = entry.Levels[k];
            Assert.Equal(new SlideSize(level.Dimensions[0], level.Dimensions[1]), geometry.GetLevelSize(scaleExponent));

            // Ruling R1: only overlap-free tiles, whose Deep Zoom rectangle is just the tile cell; overlap handling
            // is checked through DeepZoomGenerator.
            if (overlap != 0)
                continue;
            foreach (long[] t in level.TileInfo)
            {
                // [col, row, x, y, slideLevel, w, h, tileW, tileH]
                SourceRegion actual = geometry.Map(scaleExponent, t[0] * tileSize, t[1] * tileSize, t[7], t[8]);
                Assert.Equal(new SourceRegion(t[2], t[3], (int)t[4], t[5], t[6]), actual);
            }
        }
    }

    [Fact]
    public void Geometry_MalformedProperties_FallBackToDefaults()
    {
        // Level 0 is not 1000 wide, so misparsing "1e3" as 1000 would change ActiveSize.
        var source = new FakeRenderSource(
            "malformed",
            new[] { new SlideSize(1200, 800), new SlideSize(300, 200) },
            new[] { 1.0, 4.0 },
            new Dictionary<string, string>
            {
                [OpenSlideProperties.BackgroundColor] = "zzz",
                [OpenSlideProperties.BoundsX] = "abc",
                [OpenSlideProperties.BoundsWidth] = "1e3",
                [OpenSlideProperties.MppX] = "NaN",
                [OpenSlideProperties.MppY] = "-1",
            });

        var geometry = new SlideGeometry(source, limitBounds: true);

        Assert.Equal(((byte)255, (byte)255, (byte)255), geometry.Background);
        Assert.Equal((0L, 0L), (geometry.OffsetX, geometry.OffsetY));
        Assert.Equal(new SlideSize(1200, 800), geometry.ActiveSize);
        Assert.Null(geometry.MppX);
        Assert.Null(geometry.MppY);
    }

    [Fact]
    public void Geometry_NegativeBoundsSize_FallsBackToLevel0Size()
    {
        // Ruling R4: a negative bounds-width/height counts as missing (scale 1.0); zero and negative x/y stay as parsed.
        var source = new FakeRenderSource(
            "negative-bounds",
            new[] { new SlideSize(1200, 800), new SlideSize(300, 200) },
            new[] { 1.0, 4.0 },
            new Dictionary<string, string>
            {
                [OpenSlideProperties.BoundsWidth] = "-5",
                [OpenSlideProperties.BoundsHeight] = "-1",
            });

        var geometry = new SlideGeometry(source, limitBounds: true);

        Assert.Equal(new SlideSize(1200, 800), geometry.ActiveSize);
        Assert.Equal(new SlideSize(1200, 800), geometry.GetLevelSize(0));
        // The lower slide level keeps its own size too: a 2^2 view of the whole level reads all of level 1.
        Assert.Equal(new SourceRegion(0, 0, 1, 300, 200), geometry.Map(2, 0, 0, 300, 200));
    }

    [Fact]
    public void Geometry_ValidProperties_AreParsed()
    {
        var source = new FakeRenderSource(
            "valid",
            new[] { new SlideSize(1000, 800) },
            new[] { 1.0 },
            new Dictionary<string, string>
            {
                [OpenSlideProperties.BackgroundColor] = "aBcDeF",
                [OpenSlideProperties.BoundsX] = "12",
                [OpenSlideProperties.BoundsY] = "-3",
                [OpenSlideProperties.BoundsWidth] = "500",
                [OpenSlideProperties.MppX] = "0.25",
                [OpenSlideProperties.MppY] = "2.5e-1",
            });

        var bounded = new SlideGeometry(source, limitBounds: true);
        Assert.Equal(((byte)0xAB, (byte)0xCD, (byte)0xEF), bounded.Background);
        Assert.Equal((12L, -3L), (bounded.OffsetX, bounded.OffsetY));
        Assert.Equal(new SlideSize(500, 800), bounded.ActiveSize);
        Assert.Equal(0.25, bounded.MppX);
        Assert.Equal(0.25, bounded.MppY);

        var unbounded = new SlideGeometry(source, limitBounds: false);
        Assert.Equal((0L, 0L), (unbounded.OffsetX, unbounded.OffsetY));
        Assert.Equal(new SlideSize(1000, 800), unbounded.ActiveSize);
    }
}
