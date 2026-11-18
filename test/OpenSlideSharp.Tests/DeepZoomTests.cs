using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenSlideSharp.Tests.Rendering;
using Xunit;

namespace OpenSlideSharp.Tests;

public class DeepZoomTests
{
    private sealed class SetPixelsTransform : IColorTransform
    {
        public void Apply(Span<byte> pixels, int width, int height)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 3;
                pixels[i + 1] = 2;
                pixels[i + 2] = 1;
                pixels[i + 3] = 255;
            }
        }
    }

    private static string Key(GeometryOracle g) => $"{g.Source}|{g.TileSize}|{g.Overlap}|{g.LimitBounds}";

    private static string TileKey(TileOracle t) => $"{t.Source}|{t.TileSize}|{t.Overlap}|{t.Level}|{t.Col}|{t.Row}";

    public static IEnumerable<object[]> SlideCases() =>
        DeepZoomOracle.Load().Geometry.Where(g => g.Kind == "slide").Select(g => new object[] { Key(g) });

    public static IEnumerable<object[]> FakeCases() =>
        DeepZoomOracle.Load().Geometry.Where(g => g.Kind == "fake").Select(g => new object[] { Key(g) });

    public static IEnumerable<object[]> TileCases() =>
        DeepZoomOracle.Load().Tiles.Select(t => new object[] { TileKey(t) });

    private static GeometryOracle Entry(string key) => DeepZoomOracle.Load().Geometry.Single(g => Key(g) == key);

    private static void CheckMetadata(GeometryOracle entry, DeepZoomGenerator dz)
    {
        Assert.Equal(entry.LevelCount, dz.LevelCount);
        Assert.Equal(entry.TileCount, dz.TileCount);
        Assert.Equal(entry.Levels.Count, dz.LevelDimensions.Count);
        Assert.Equal(entry.Levels.Count, dz.LevelTiles.Count);
        Assert.Equal(entry.DziJpeg, dz.GetDzi("jpeg"));
        Assert.Equal(entry.DziPng, dz.GetDzi("png"));
        for (int level = 0; level < entry.Levels.Count; level++)
        {
            LevelOracle expected = entry.Levels[level];
            Assert.Equal(new SlideSize(expected.Dimensions[0], expected.Dimensions[1]), dz.LevelDimensions[level]);
            Assert.Equal(new TileGridSize(expected.Tiles[0], expected.Tiles[1]), dz.LevelTiles[level]);
            foreach (long[] t in expected.TileInfo)
            {
                string where = $"{entry.Source} level {level} tile {t[0]},{t[1]}";
                Assert.True(
                    new TileRegion(t[2], t[3], (int)t[4], t[5], t[6]) == dz.GetTileCoordinates(level, t[0], t[1]),
                    where + " coordinates");
                Assert.True(new SlideSize(t[7], t[8]) == dz.GetTileDimensions(level, t[0], t[1]), where + " dimensions");
            }
        }
    }

    [Theory]
    [MemberData(nameof(SlideCases))]
    public void Metadata_MatchesPython(string key)
    {
        GeometryOracle entry = Entry(key);
        using var osr = OpenSlide.Open(TestFiles.Get(entry.Source));
        CheckMetadata(entry, new DeepZoomGenerator(osr, entry.TileSize, entry.Overlap, entry.LimitBounds));
    }

    [Theory]
    [MemberData(nameof(FakeCases))]
    public void Metadata_MatchesPython_ForFakes(string key)
    {
        GeometryOracle entry = Entry(key);
        var dz = new DeepZoomGenerator(FakeRenderSource.FromOracle(entry), entry.TileSize, entry.Overlap, entry.LimitBounds, null);
        CheckMetadata(entry, dz);
    }

    [Theory]
    [MemberData(nameof(TileCases))]
    public void GetTile_MatchesPython(string key)
    {
        TileOracle entry = DeepZoomOracle.Load().Tiles.Single(t => TileKey(t) == key);
        string[] parts = entry.Source.Split('#');
        using var osr = OpenSlide.Open(TestFiles.Get(parts[0]));
        DeepZoomGenerator dz = parts.Length == 1
            ? new DeepZoomGenerator(osr, entry.TileSize, entry.Overlap)
            : new DeepZoomGenerator(osr.AssociatedImages[parts[1]], entry.TileSize, entry.Overlap);

        PixelImage tile = dz.GetTile(entry.Level, entry.Col, entry.Row);

        Assert.Equal(PixelFormat.Bgra32Opaque, tile.Format);
        Assert.Equal((entry.ExpectedSize[0], entry.ExpectedSize[1]), (tile.Width, tile.Height));
        Assert.Equal(new SlideSize(tile.Width, tile.Height), dz.GetTileDimensions(entry.Level, entry.Col, entry.Row));
        Assert.Equal(entry.IccLength, tile.IccProfile.Length);
        if (entry.Exact)
        {
            var expected = OracleData.LoadPng(entry.File);
            Assert.Equal((expected.Width, expected.Height), (tile.Width, tile.Height));
            for (int i = 0; i < expected.Bgra.Length; i++)
            {
                if (i % 4 != 3 && tile.Data[i] != expected.Bgra[i])
                {
                    Assert.Fail($"{entry.File}: byte {i} (pixel {i / 4}, channel {i % 4}) expected {expected.Bgra[i]} got {tile.Data[i]}");
                }
            }
        }
    }

    [Fact]
    public void GetTile_Boxes_Level9_1_0_Is47x250()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        PixelImage tile = new DeepZoomGenerator(osr).GetTile(9, 1, 0);
        Assert.Equal((47, 250), (tile.Width, tile.Height));
    }

    [Fact]
    public void TileColorProfile_Is588Bytes()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(588, new DeepZoomGenerator(osr).GetTile(9, 1, 0).IccProfile.Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void GetTile_BadLevel_Throws(int level)
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var dz = new DeepZoomGenerator(osr);
        Assert.Equal("level", Assert.Throws<ArgumentOutOfRangeException>(() => dz.GetTile(level, 0, 0)).ParamName);
    }

    [Theory]
    [InlineData(0, -1, "row")]
    [InlineData(0, 1, "row")]
    [InlineData(-1, 0, "column")]
    [InlineData(1, 0, "column")]
    public void GetTile_BadAddress_Throws(long column, long row, string param)
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var dz = new DeepZoomGenerator(osr);
        Assert.Equal(param, Assert.Throws<ArgumentOutOfRangeException>(() => dz.GetTile(0, column, row)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => dz.GetTileCoordinates(0, column, row));
        Assert.Throws<ArgumentOutOfRangeException>(() => dz.GetTileDimensions(0, column, row));
    }

    [Fact]
    public void GetTileCoordinates_Level9_1_0()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(new TileRegion(253, 0, 0, 47, 250), new DeepZoomGenerator(osr).GetTileCoordinates(9, 1, 0));
    }

    [Fact]
    public void GetDzi_ContainsNamespace()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Contains("http://schemas.microsoft.com/deepzoom/2008", new DeepZoomGenerator(osr).GetDzi("png"));
    }

    [Fact]
    public void ToString_DescribesParameters()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(
            $"DeepZoomGenerator(OpenSlide({TestFiles.Get("boxes.tiff")}), tileSize: 254, overlap: 1, limitBounds: False)",
            new DeepZoomGenerator(osr).ToString());
    }

    [Fact]
    public void GetDzi_EscapesFormat()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var dz = new DeepZoomGenerator(osr);
        Assert.Contains("Format=\"a&amp;b&quot;&lt;&gt;&#10;\"", dz.GetDzi("a&b\"<>\n"));
        Assert.Contains("Format=\"&#13;&#09;\"", dz.GetDzi("\r\t"));
        Assert.Throws<ArgumentNullException>(() => dz.GetDzi(null!));
    }

    [Fact]
    public void GetTile_WithColorTransform_AppliesAndDropsProfile()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var dz = new DeepZoomGenerator(osr, colorTransform: new SetPixelsTransform());
        PixelImage tile = dz.GetTile(9, 1, 0);
        Assert.True(tile.IccProfile.IsEmpty);
        for (int i = 0; i < tile.Data.Length; i += 4)
        {
            Assert.Equal(new byte[] { 3, 2, 1, 255 }, tile.Data.AsSpan(i, 4).ToArray());
        }
    }

    [Fact]
    public void GetTile_Parallel_MatchesSequential()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var dz = new DeepZoomGenerator(osr, 64, 5);
        var addresses = new List<(int Level, long Col, long Row)>();
        for (int level = Math.Max(0, dz.LevelCount - 4); level < dz.LevelCount; level++)
            for (long r = 0; r < dz.LevelTiles[level].Rows; r++)
                for (long c = 0; c < dz.LevelTiles[level].Columns; c++)
                    addresses.Add((level, c, r));
        byte[][] sequential = addresses.Select(a => dz.GetTile(a.Level, a.Col, a.Row).Data).ToArray();
        var parallel = new byte[addresses.Count][];

        Parallel.For(0, addresses.Count, i => parallel[i] = dz.GetTile(addresses[i].Level, addresses[i].Col, addresses[i].Row).Data);

        for (int i = 0; i < addresses.Count; i++)
            Assert.True(sequential[i].AsSpan().SequenceEqual(parallel[i]), $"tile {addresses[i]}");
    }

    [Fact]
    public void GetTile_AfterSlideDisposed_ThrowsObjectDisposed()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var dz = new DeepZoomGenerator(osr);
        osr.Dispose();
        for (int level = 0; level < dz.LevelCount; level++)
            Assert.Throws<ObjectDisposedException>(() => dz.GetTile(level, 0, 0));
        Assert.Throws<ObjectDisposedException>(() => new DeepZoomGenerator(osr));
    }

    [Fact]
    public void GetTile_AssociatedImage_AfterSlideDisposed_ThrowsObjectDisposed()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes_0.dcm"));
        AssociatedImage thumbnail = osr.AssociatedImages["thumbnail"];
        var dz = new DeepZoomGenerator(thumbnail, 4, 1);
        osr.Dispose();
        for (int level = 0; level < dz.LevelCount; level++)
            Assert.Throws<ObjectDisposedException>(() => dz.GetTile(level, 0, 0));
        Assert.Throws<ObjectDisposedException>(() => new DeepZoomGenerator(thumbnail, 4, 1));
    }

    [Fact]
    public void LevelLists_CannotBeChangedThroughACast()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var dz = new DeepZoomGenerator(osr);

        // Writable views would let callers corrupt the grid that GetTile validates addresses against.
        Assert.IsNotType<TileGridSize[]>(dz.LevelTiles);
        Assert.IsNotType<SlideSize[]>(dz.LevelDimensions);
        Assert.Throws<NotSupportedException>(() => ((IList<TileGridSize>)dz.LevelTiles)[0] = new TileGridSize(9, 9));
        Assert.Throws<NotSupportedException>(() => ((IList<SlideSize>)dz.LevelDimensions)[0] = new SlideSize(9, 9));
    }

    [Fact]
    public void Constructor_HugeTileSizeAndOverlap_AreAccepted()
    {
        // openslide-python accepts any tile size and overlap. Here every level is one tile, which has no interior
        // edge, so the overlap adds nothing and each tile is the whole level.
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var whole = new DeepZoomGenerator(osr, tileSize: int.MaxValue, overlap: int.MaxValue);
        var standard = new DeepZoomGenerator(osr);

        Assert.All(whole.LevelTiles, t => Assert.Equal(new TileGridSize(1, 1), t));
        Assert.Equal(whole.LevelCount, whole.TileCount);
        Assert.Contains($"TileSize=\"{int.MaxValue}\" Overlap=\"{int.MaxValue}\"", whole.GetDzi("png"));
        int top = whole.LevelCount - 1;
        PixelImage level = whole.GetTile(top, 0, 0);
        Assert.Equal(whole.LevelDimensions[top], new SlideSize(level.Width, level.Height));
        Assert.Equal(new TileRegion(0, 0, 0, 300, 250), whole.GetTileCoordinates(top, 0, 0));

        // A level that is one tile with the default size too gives the same pixels.
        Assert.Equal(new TileGridSize(1, 1), standard.LevelTiles[top - 1]);
        Assert.Equal(standard.GetTile(top - 1, 0, 0).Data, whole.GetTile(top - 1, 0, 0).Data);
    }

    [Fact]
    public void GetTile_TooLargeForAnArray_ThrowsForLevelBeforeReading()
    {
        // 40000 x 40000 pixels need 6.4 GB, more than a .NET array holds.
        var source = FakeRenderSource.SingleLevel(100_000, 100_000);
        var dz = new DeepZoomGenerator(source, 40_000, 0, false, null);
        int top = dz.LevelCount - 1;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => dz.GetTile(top, 0, 0));

        Assert.Equal("level", ex.ParamName);
        Assert.Contains("40000 x 40000", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, source.ReadCount);
        Assert.Equal(new SlideSize(40_000, 40_000), dz.GetTileDimensions(top, 0, 0));
    }

    [Fact]
    public void GetTile_HugeOverlap_ReportsTheSizeAndThrowsForLevel()
    {
        // An interior tile is the tile size plus twice the overlap: more than int.MaxValue pixels wide here, which
        // GetTileDimensions and GetTileCoordinates report as is, like openslide-python.
        const int overlap = int.MaxValue / 2;
        var source = FakeRenderSource.SingleLevel(1000, 1000);
        var dz = new DeepZoomGenerator(source, 254, overlap, false, null);
        int top = dz.LevelCount - 1;
        long edge = 254 + (2L * overlap);

        Assert.Equal(new SlideSize(edge, edge), dz.GetTileDimensions(top, 1, 1));
        Assert.Equal(new TileRegion(254 - overlap, 254 - overlap, 0, 1000 - 254 + overlap, 1000 - 254 + overlap), dz.GetTileCoordinates(top, 1, 1));
        Assert.Equal("level", Assert.Throws<ArgumentOutOfRangeException>(() => dz.GetTile(top, 1, 1)).ParamName);
        Assert.Equal(0, source.ReadCount);

        // The tiles of a level with one tile have no overlap, so they render.
        Assert.Equal(new TileGridSize(1, 1), dz.LevelTiles[top - 2]);
        PixelImage small = dz.GetTile(top - 2, 0, 0);
        Assert.Equal(dz.LevelDimensions[top - 2], new SlideSize(small.Width, small.Height));
    }

    [Fact]
    public void Constructor_InvalidParameters_Throw()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal("tileSize", Assert.Throws<ArgumentOutOfRangeException>(() => new DeepZoomGenerator(osr, tileSize: 0)).ParamName);
        Assert.Equal("overlap", Assert.Throws<ArgumentOutOfRangeException>(() => new DeepZoomGenerator(osr, overlap: -1)).ParamName);
        Assert.Throws<ArgumentNullException>("slide", () => new DeepZoomGenerator((OpenSlide)null!));
        Assert.Throws<ArgumentNullException>("image", () => new DeepZoomGenerator((AssociatedImage)null!));
    }
}
