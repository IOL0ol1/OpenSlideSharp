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
    public void Constructor_InvalidParameters_Throw()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal("tileSize", Assert.Throws<ArgumentOutOfRangeException>(() => new DeepZoomGenerator(osr, tileSize: 0)).ParamName);
        Assert.Equal("overlap", Assert.Throws<ArgumentOutOfRangeException>(() => new DeepZoomGenerator(osr, overlap: -1)).ParamName);
        Assert.Throws<ArgumentNullException>("slide", () => new DeepZoomGenerator((OpenSlide)null!));
        Assert.Throws<ArgumentNullException>("image", () => new DeepZoomGenerator((AssociatedImage)null!));
    }
}
