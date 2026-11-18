using System.Linq;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class TileGridTests
{
    private static TileGrid BoxesGrid()
    {
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        return new TileGrid(slide.LevelDimensions, slide.LevelDownsamples);
    }

    [Fact]
    public void TileGrid_VisibleTiles_CoversViewExactly()
    {
        TileGrid grid = BoxesGrid();

        TileKey[] tiles = grid.VisibleTiles(0, 0, 0, 300, 250).ToArray();

        Assert.Equal([new TileKey(0, 0, 0), new TileKey(0, 1, 0)], tiles);
        Assert.Equal((0L, 0L, 256, 250), grid.Region(tiles[0]));
        Assert.Equal((256L, 0L, 44, 250), grid.Region(tiles[1]));
        Assert.Equal((0.0, 0.0, 256.0, 250.0), grid.Bounds(tiles[0]));
        Assert.Equal((256.0, 0.0, 300.0, 250.0), grid.Bounds(tiles[1]));

        // Only the tiles the view touches: the right and bottom edges are exclusive.
        Assert.Equal([new TileKey(0, 0, 0)], grid.VisibleTiles(0, 100, 10, 200, 20));
        Assert.Equal([new TileKey(0, 0, 0)], grid.VisibleTiles(0, 0, 0, 256, 250));
        Assert.Equal([new TileKey(0, 1, 0)], grid.VisibleTiles(0, 256, 0, 300, 250));
        Assert.Equal([new TileKey(0, 0, 0), new TileKey(0, 1, 0)], grid.VisibleTiles(0, 255.5, 0, 256.5, 1));
    }

    [Fact]
    public void TileGrid_Region_UsesLevelDownsample()
    {
        TileGrid grid = BoxesGrid();

        // Level 2 is 75 x 62 with downsample 4.016129032258064.
        Assert.Equal([new TileKey(2, 0, 0)], grid.VisibleTiles(2, 0, 0, 300, 250));
        Assert.Equal((0L, 0L, 75, 62), grid.Region(new TileKey(2, 0, 0)));
        (double left, double top, double right, double bottom) = grid.Bounds(new TileKey(2, 0, 0));
        Assert.Equal((0.0, 0.0), (left, top));
        Assert.Equal(75 * 4.016129032258064, right, 1e-9);
        Assert.Equal(62 * 4.016129032258064, bottom, 1e-9);

        // Away from the origin the level-0 corner is the level offset times the downsample.
        var synthetic = new TileGrid([new SlideSize(1000, 600), new SlideSize(400, 240)], [1.0, 2.5]);
        Assert.Equal(
            [new TileKey(1, 0, 0), new TileKey(1, 1, 0)],
            synthetic.VisibleTiles(1, 0, 0, 1000, 600));
        Assert.Equal((640L, 0L, 144, 240), synthetic.Region(new TileKey(1, 1, 0)));
        Assert.Equal((640.0, 0.0, 1000.0, 600.0), synthetic.Bounds(new TileKey(1, 1, 0)));
        Assert.Equal([new TileKey(1, 1, 0)], synthetic.VisibleTiles(1, 700, 100, 900, 200));
    }

    [Fact]
    public void TileGrid_OutsideSlide_NoTiles()
    {
        TileGrid grid = BoxesGrid();

        Assert.Empty(grid.VisibleTiles(0, 300, 0, 400, 250));
        Assert.Empty(grid.VisibleTiles(0, 0, 250, 300, 500));
        Assert.Empty(grid.VisibleTiles(0, -100, -100, 0, 0));
        Assert.Empty(grid.VisibleTiles(3, 1000, 1000, 2000, 2000));
        Assert.Empty(grid.VisibleTiles(0, 100, 100, 100, 200)); // no width
        Assert.Empty(grid.VisibleTiles(0, 200, 100, 100, 200)); // inverted
        Assert.Empty(grid.VisibleTiles(0, double.NaN, 0, 10, 10));
        Assert.Empty(grid.VisibleTiles(-1, 0, 0, 300, 250));
        Assert.Empty(grid.VisibleTiles(4, 0, 0, 300, 250));

        // A view partly outside the slide is clipped to it.
        Assert.Equal([new TileKey(0, 0, 0)], grid.VisibleTiles(0, -100, -100, 50, 50));
        Assert.Equal([new TileKey(0, 1, 0)], grid.VisibleTiles(0, 290, 200, 5000, 5000));
    }
}
