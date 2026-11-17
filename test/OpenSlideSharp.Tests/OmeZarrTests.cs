using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Json.Schema;
using OpenSlideSharp.Tests.Rendering;
using Xunit;

namespace OpenSlideSharp.Tests;

public class OmeZarrTests
{
    private const string MppX = "openslide.mpp-x";
    private const string MppY = "openslide.mpp-y";

    private static readonly SlideSize[] BoxesLevels =
    {
        new(300, 250), new(150, 125), new(75, 63), new(38, 32),
    };

    // Chunks per channel and level for boxes.tiff with ChunkSize = 64: 5x4, 3x2, 2x1, 1x1.
    private static readonly (int Columns, int Rows)[] BoxesGrid = { (5, 4), (3, 2), (2, 1), (1, 1) };

    private static OpenSlide OpenBoxes() => OpenSlide.Open(TestFiles.Get("boxes.tiff"));

    private static OmeZarrOptions Options(
        NgffVersion version = NgffVersion.V0_5,
        OmeZarrCompression compression = OmeZarrCompression.None,
        int chunkSize = 64) =>
        new() { Version = version, Compression = compression, ChunkSize = chunkSize };

    private static string EntryText(OmeZarrGenerator generator, string key)
    {
        Assert.True(generator.TryGetEntry(key, out byte[]? content), key);
        return Encoding.UTF8.GetString(content);
    }

    private static JsonElement Entry(OmeZarrGenerator generator, string key) =>
        JsonDocument.Parse(EntryText(generator, key)).RootElement;

    private static double[] Numbers(JsonElement array) => array.EnumerateArray().Select(e => e.GetDouble()).ToArray();

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static JsonElement Multiscale(OmeZarrGenerator generator) =>
        generator.Version == NgffVersion.V0_5
            ? Entry(generator, "zarr.json").GetProperty("attributes").GetProperty("ome").GetProperty("multiscales")[0]
            : Entry(generator, ".zattrs").GetProperty("multiscales")[0];

    private static JsonElement[] Transforms(JsonElement multiscale, int level) =>
        multiscale.GetProperty("datasets")[level].GetProperty("coordinateTransformations").EnumerateArray().ToArray();

    // Every chunk, in EnumerateKeys order: level, row, column, then the three channels of that chunk.
    private static IEnumerable<(int Level, int Channel, int Y, int X)> AllChunks(OmeZarrGenerator generator)
    {
        for (int level = 0; level < generator.LevelCount; level++)
        {
            SlideSize size = generator.LevelDimensions[level];
            int rows = (int)((size.Height + generator.ChunkSize - 1) / generator.ChunkSize);
            int columns = (int)((size.Width + generator.ChunkSize - 1) / generator.ChunkSize);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                    for (int channel = 0; channel < 3; channel++)
                        yield return (level, channel, y, x);
        }
    }

    private static FakeRenderSource BoundsFake(params (string Name, string Value)[] extra)
    {
        FakeSlideOracle fake = DeepZoomOracle.Load().Geometry.First(g => g.Source == "pyramid4x-bounds").Fake!;
        var properties = new Dictionary<string, string>(fake.Properties);
        foreach (var (name, value) in extra)
            properties[name] = value;
        return new FakeRenderSource(
            "pyramid4x-bounds", fake.LevelDimensions.Select(d => new SlideSize(d[0], d[1])), fake.LevelDownsamples, properties);
    }

    // ---- schemas ------------------------------------------------------------------------------------------

    private static (JsonSchema Image, JsonSchema Strict) LoadSchemas(string version)
    {
        // Each call builds into its own registry; referenced schemas must be registered (built) before the
        // schemas that $ref them.
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        string dir = Path.Combine(AppContext.BaseDirectory, "Schemas", "ngff-" + version);
        if (version == "0.5")
            JsonSchema.FromFile(Path.Combine(dir, "_version.schema"), options);
        JsonSchema image = JsonSchema.FromFile(Path.Combine(dir, "image.schema"), options);
        JsonSchema strict = JsonSchema.FromFile(Path.Combine(dir, "strict_image.schema"), options);
        return (image, strict);
    }

    private static void AssertValid(JsonSchema schema, JsonNode instance, bool expected, string what)
    {
        using JsonDocument document = JsonDocument.Parse(instance.ToJsonString());
        EvaluationResults results = schema.Evaluate(
            document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        string errors = string.Join(
            "; ",
            (results.Details ?? new List<EvaluationResults>())
                .Where(d => d.Errors is not null)
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}")));
        Assert.True(results.IsValid == expected, $"{what}: expected valid = {expected}. {errors}");
    }

    // ---- levels ---------------------------------------------------------------------------------------------

    [Fact]
    public void Levels_HalveUntilOneChunk()
    {
        using OpenSlide osr = OpenBoxes();

        var generator = new OmeZarrGenerator(osr, Options());
        Assert.Equal(4, generator.LevelCount);
        Assert.Equal(BoxesLevels, generator.LevelDimensions);
        Assert.False(generator.LevelDimensions is SlideSize[], "LevelDimensions must not expose the internal array");

        var single = new OmeZarrGenerator(osr, Options(chunkSize: 512));
        Assert.Equal(1, single.LevelCount);
        Assert.Equal(new[] { new SlideSize(300, 250) }, single.LevelDimensions);
    }

    // ---- group metadata -------------------------------------------------------------------------------------

    [Fact]
    public void GroupMetadata_V05_ValidatesAgainstSchema()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options());
        JsonNode attributes = JsonNode.Parse(EntryText(generator, "zarr.json"))!["attributes"]!;
        var (image, strict) = LoadSchemas("0.5");

        AssertValid(image, attributes, true, "image.schema");
        AssertValid(strict, attributes, true, "strict_image.schema");

        // The validator is not vacuous: the $ref'd version schema and the strict-only requirements both bite.
        JsonNode wrongVersion = attributes.DeepClone();
        wrongVersion["ome"]!["version"] = "0.4";
        AssertValid(image, wrongVersion, false, "image.schema, version 0.4");
        JsonNode unnamed = attributes.DeepClone();
        unnamed["ome"]!["multiscales"]![0]!.AsObject().Remove("name");
        AssertValid(image, unnamed, true, "image.schema, no name");
        AssertValid(strict, unnamed, false, "strict_image.schema, no name");
    }

    [Fact]
    public void GroupMetadata_V04_ValidatesAgainstSchema()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options(NgffVersion.V0_4));
        JsonNode attributes = JsonNode.Parse(EntryText(generator, ".zattrs"))!;
        var (image, strict) = LoadSchemas("0.4");

        AssertValid(image, attributes, true, "image.schema");
        AssertValid(strict, attributes, true, "strict_image.schema");

        JsonNode wrongVersion = attributes.DeepClone();
        wrongVersion["multiscales"]![0]!["version"] = "0.5";
        AssertValid(image, wrongVersion, false, "image.schema, version 0.5");
        JsonNode unnamed = attributes.DeepClone();
        unnamed["multiscales"]![0]!.AsObject().Remove("name");
        AssertValid(image, unnamed, true, "image.schema, no name");
        AssertValid(strict, unnamed, false, "strict_image.schema, no name");
    }

    [Fact]
    public void GroupMetadata_V05_Values()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options());
        string text = EntryText(generator, "zarr.json");
        JsonElement root = JsonDocument.Parse(text).RootElement;

        // Compact: re-serializing the parsed document (which keeps number text as is) gives the same text.
        Assert.Equal(text, JsonSerializer.Serialize(root));

        Assert.Equal(3, root.GetProperty("zarr_format").GetInt32());
        Assert.Equal("group", root.GetProperty("node_type").GetString());
        JsonElement ome = root.GetProperty("attributes").GetProperty("ome");
        Assert.Equal("0.5", ome.GetProperty("version").GetString());

        JsonElement multiscale = Multiscale(generator);
        Assert.Equal(1, ome.GetProperty("multiscales").GetArrayLength());
        Assert.False(multiscale.TryGetProperty("version", out _), "0.5 keeps the version in ome, not in multiscales");
        Assert.Equal("boxes.tiff", multiscale.GetProperty("name").GetString());
        Assert.Equal("lanczos", multiscale.GetProperty("type").GetString());
        JsonElement metadata = multiscale.GetProperty("metadata");
        Assert.Equal("OpenSlideSharp", metadata.GetProperty("method").GetString());
        Assert.Equal("Rendered from OpenSlide levels with Lanczos-3 resampling", metadata.GetProperty("description").GetString());

        JsonElement[] axes = multiscale.GetProperty("axes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "c", "y", "x" }, axes.Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(new[] { "channel", "space", "space" }, axes.Select(a => a.GetProperty("type").GetString()));
        Assert.False(axes[0].TryGetProperty("unit", out _));
        Assert.Equal("micrometer", axes[1].GetProperty("unit").GetString());
        Assert.Equal("micrometer", axes[2].GetProperty("unit").GetString());

        double m = double.Parse("352.77777983377013", CultureInfo.InvariantCulture);
        Assert.Equal(4, multiscale.GetProperty("datasets").GetArrayLength());
        for (int level = 0; level < 4; level++)
        {
            Assert.Equal(level.ToString(CultureInfo.InvariantCulture), multiscale.GetProperty("datasets")[level].GetProperty("path").GetString());
            JsonElement[] transforms = Transforms(multiscale, level);
            Assert.Single(transforms); // no translation without LimitBounds
            Assert.Equal("scale", transforms[0].GetProperty("type").GetString());
            double s = m * (1 << level);
            Assert.Equal(new[] { 1.0, s, s }, Numbers(transforms[0].GetProperty("scale")));
        }

        Assert.Equal(new[] { 1.0, m, m }, Numbers(Transforms(multiscale, 0)[0].GetProperty("scale")));
        Assert.Equal(new[] { 1.0, 8 * m, 8 * m }, Numbers(Transforms(multiscale, 3)[0].GetProperty("scale")));
        Assert.Contains("\"scale\":[1,352.7777798337701,352.7777798337701]", text);
        Assert.Contains("\"scale\":[1,2822.222238670161,2822.222238670161]", text);

        JsonElement omero = ome.GetProperty("omero");
        JsonElement[] channels = omero.GetProperty("channels").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Red", "Green", "Blue" }, channels.Select(c => c.GetProperty("label").GetString()));
        Assert.Equal(new[] { "FF0000", "00FF00", "0000FF" }, channels.Select(c => c.GetProperty("color").GetString()));
        foreach (JsonElement channel in channels)
        {
            JsonElement window = channel.GetProperty("window");
            Assert.Equal(0, window.GetProperty("start").GetInt32());
            Assert.Equal(255, window.GetProperty("end").GetInt32());
            Assert.Equal(0, window.GetProperty("min").GetInt32());
            Assert.Equal(255, window.GetProperty("max").GetInt32());
            Assert.True(channel.GetProperty("active").GetBoolean());
            Assert.Equal(1, channel.GetProperty("coefficient").GetInt32());
            Assert.Equal("linear", channel.GetProperty("family").GetString());
            Assert.False(channel.GetProperty("inverted").GetBoolean());
        }

        Assert.Equal("color", omero.GetProperty("rdefs").GetProperty("model").GetString());
    }

    [Fact]
    public void GroupMetadata_V04_Values()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options(NgffVersion.V0_4));

        JsonElement group = Entry(generator, ".zgroup");
        Assert.Equal("{\"zarr_format\":2}", EntryText(generator, ".zgroup"));
        Assert.Equal(2, group.GetProperty("zarr_format").GetInt32());

        JsonElement attributes = Entry(generator, ".zattrs");
        JsonElement multiscale = Multiscale(generator);
        Assert.Equal("0.4", multiscale.GetProperty("version").GetString());
        Assert.Equal("boxes.tiff", multiscale.GetProperty("name").GetString());
        Assert.Equal(4, multiscale.GetProperty("datasets").GetArrayLength());
        double m = double.Parse("352.77777983377013", CultureInfo.InvariantCulture);
        Assert.Equal(new[] { 1.0, 4 * m, 4 * m }, Numbers(Transforms(multiscale, 2)[0].GetProperty("scale")));
        Assert.Equal(3, attributes.GetProperty("omero").GetProperty("channels").GetArrayLength());
    }

    [Fact]
    public void Metadata_LimitBounds_AddsTranslation()
    {
        FakeRenderSource source = BoundsFake((MppX, "0.25"), (MppY, "0.25"));
        var options = Options();
        options.LimitBounds = true;
        var generator = new OmeZarrGenerator(source, "pyramid4x-bounds", false, options);

        Assert.Equal(new SlideSize(50001, 40003), generator.LevelDimensions[0]);
        JsonElement multiscale = Multiscale(generator);
        Assert.Equal("pyramid4x-bounds", multiscale.GetProperty("name").GetString());
        for (int level = 0; level < generator.LevelCount; level++)
        {
            JsonElement[] transforms = Transforms(multiscale, level);
            Assert.Equal(2, transforms.Length);
            double s = 0.25 * Math.Pow(2, level);
            Assert.Equal(new[] { 1.0, s, s }, Numbers(transforms[0].GetProperty("scale")));
            Assert.Equal("translation", transforms[1].GetProperty("type").GetString());
            Assert.Equal(new[] { 0, 141.75, 308.5 }, Numbers(transforms[1].GetProperty("translation")));
        }

        // Without LimitBounds the same slide has no translation.
        var unbounded = new OmeZarrGenerator(source, "pyramid4x-bounds", false, Options());
        Assert.Equal(new SlideSize(100000, 80000), unbounded.LevelDimensions[0]);
        Assert.Single(Transforms(Multiscale(unbounded), 0));
    }

    [Fact]
    public void Metadata_LimitBounds_WithoutMpp_TranslatesInPixels()
    {
        var options = Options(NgffVersion.V0_4);
        options.LimitBounds = true;
        var generator = new OmeZarrGenerator(BoundsFake(), "pyramid4x-bounds", false, options);

        JsonElement[] transforms = Transforms(Multiscale(generator), 1);
        Assert.Equal(new[] { 1.0, 2, 2 }, Numbers(transforms[0].GetProperty("scale")));
        Assert.Equal(new[] { 0.0, 567, 1234 }, Numbers(transforms[1].GetProperty("translation")));
    }

    [Fact]
    public void Metadata_InvalidMpp_UsesPixelUnits()
    {
        // mpp-y alone is valid: physical units need both axes, so the y scale stays in pixels too.
        FakeRenderSource source = FakeRenderSource.SingleLevel(300, 250, (MppX, "NaN"), (MppY, "0.5"));
        var generator = new OmeZarrGenerator(source, "nan-mpp", false, Options());

        Assert.Equal(BoxesLevels, generator.LevelDimensions);
        JsonElement multiscale = Multiscale(generator);
        foreach (JsonElement axis in multiscale.GetProperty("axes").EnumerateArray())
            Assert.False(axis.TryGetProperty("unit", out _), axis.ToString());
        Assert.Equal(new[] { 1.0, 1, 1 }, Numbers(Transforms(multiscale, 0)[0].GetProperty("scale")));
        Assert.Equal(new[] { 1.0, 4, 4 }, Numbers(Transforms(multiscale, 2)[0].GetProperty("scale")));
    }

    [Fact]
    public void Metadata_EscapesName()
    {
        var generator = new OmeZarrGenerator(
            FakeRenderSource.SingleLevel(10, 10), "a\"b\\c\u0001\n\t\u00e9", false, Options());

        Assert.Equal("a\"b\\c\u0001\n\t\u00e9", Multiscale(generator).GetProperty("name").GetString());
        Assert.Contains("\"name\":\"a\\\"b\\\\c\\u0001\\n\\t\u00e9\"", EntryText(generator, "zarr.json"));
    }

    // ---- array metadata -------------------------------------------------------------------------------------

    [Fact]
    public void ArrayMetadata_V05_Values()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options());

        for (int level = 0; level < 4; level++)
        {
            string text = EntryText(generator, $"{level}/zarr.json");
            JsonElement array = JsonDocument.Parse(text).RootElement;
            Assert.Equal(text, JsonSerializer.Serialize(array));
            Assert.Equal(3, array.GetProperty("zarr_format").GetInt32());
            Assert.Equal("array", array.GetProperty("node_type").GetString());
            Assert.Equal(new[] { 3.0, BoxesLevels[level].Height, BoxesLevels[level].Width }, Numbers(array.GetProperty("shape")));
            Assert.Equal("uint8", array.GetProperty("data_type").GetString());
            JsonElement grid = array.GetProperty("chunk_grid");
            Assert.Equal("regular", grid.GetProperty("name").GetString());
            Assert.Equal(new[] { 1.0, 64, 64 }, Numbers(grid.GetProperty("configuration").GetProperty("chunk_shape")));
            JsonElement encoding = array.GetProperty("chunk_key_encoding");
            Assert.Equal("default", encoding.GetProperty("name").GetString());
            Assert.Equal("/", encoding.GetProperty("configuration").GetProperty("separator").GetString());
            Assert.Equal(0, array.GetProperty("fill_value").GetInt32());
            JsonElement[] codecs = array.GetProperty("codecs").EnumerateArray().ToArray();
            Assert.Single(codecs);
            Assert.Equal("bytes", codecs[0].GetProperty("name").GetString());
            Assert.Equal("little", codecs[0].GetProperty("configuration").GetProperty("endian").GetString());
            Assert.Equal(new[] { "c", "y", "x" }, Strings(array.GetProperty("dimension_names")));
        }

        var options = Options(compression: OmeZarrCompression.Gzip);
        options.GzipLevel = 3;
        JsonElement[] gzipCodecs = Entry(new OmeZarrGenerator(osr, options), "1/zarr.json")
            .GetProperty("codecs").EnumerateArray().ToArray();
        Assert.Equal(2, gzipCodecs.Length);
        Assert.Equal("bytes", gzipCodecs[0].GetProperty("name").GetString());
        Assert.Equal("gzip", gzipCodecs[1].GetProperty("name").GetString());
        Assert.Equal(3, gzipCodecs[1].GetProperty("configuration").GetProperty("level").GetInt32());
    }

    [Fact]
    public void ArrayMetadata_V04_Values()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options(NgffVersion.V0_4));

        for (int level = 0; level < 4; level++)
        {
            string text = EntryText(generator, $"{level}/.zarray");
            JsonElement array = JsonDocument.Parse(text).RootElement;
            Assert.Equal(text, JsonSerializer.Serialize(array));
            Assert.Equal(new[] { 1.0, 64, 64 }, Numbers(array.GetProperty("chunks")));
            Assert.Equal(JsonValueKind.Null, array.GetProperty("compressor").ValueKind);
            Assert.Equal("/", array.GetProperty("dimension_separator").GetString());
            Assert.Equal("|u1", array.GetProperty("dtype").GetString());
            Assert.Equal(0, array.GetProperty("fill_value").GetInt32());
            Assert.Equal(JsonValueKind.Null, array.GetProperty("filters").ValueKind);
            Assert.Equal("C", array.GetProperty("order").GetString());
            Assert.Equal(new[] { 3.0, BoxesLevels[level].Height, BoxesLevels[level].Width }, Numbers(array.GetProperty("shape")));
            Assert.Equal(2, array.GetProperty("zarr_format").GetInt32());
        }

        var options = Options(NgffVersion.V0_4, OmeZarrCompression.Gzip);
        options.GzipLevel = 9;
        JsonElement compressor = Entry(new OmeZarrGenerator(osr, options), "3/.zarray").GetProperty("compressor");
        Assert.Equal("gzip", compressor.GetProperty("id").GetString());
        Assert.Equal(9, compressor.GetProperty("level").GetInt32());
    }

    // ---- chunks ---------------------------------------------------------------------------------------------

    [Fact]
    public void GetChunk_EqualsDeepZoomTile()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options());
        var dz = new DeepZoomGenerator(osr, 64, 0);
        const int cs = 64;

        int checkedChunks = 0;
        for (int level = 0; level < generator.LevelCount; level++)
        {
            int dzLevel = dz.LevelCount - 1 - level;
            Assert.Equal(generator.LevelDimensions[level], dz.LevelDimensions[dzLevel]);
            Assert.Equal(new TileGridSize(BoxesGrid[level].Columns, BoxesGrid[level].Rows), dz.LevelTiles[dzLevel]);
            for (int cy = 0; cy < BoxesGrid[level].Rows; cy++)
            {
                for (int cx = 0; cx < BoxesGrid[level].Columns; cx++)
                {
                    PixelImage tile = dz.GetTile(dzLevel, cx, cy);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        byte[] chunk = generator.GetChunk(level, channel, cy, cx);
                        Assert.Equal(cs * cs, chunk.Length);
                        for (int row = 0; row < cs; row++)
                        {
                            for (int col = 0; col < cs; col++)
                            {
                                byte expected = row < tile.Height && col < tile.Width
                                    ? tile.Data[(((row * tile.Width) + col) * 4) + (2 - channel)]
                                    : (byte)0;
                                byte actual = chunk[(row * cs) + col];
                                if (actual != expected)
                                    Assert.Fail($"level {level} chunk ({cy}, {cx}) channel {channel} pixel ({row}, {col}): expected {expected}, got {actual}");
                            }
                        }

                        checkedChunks++;
                    }
                }
            }
        }

        Assert.Equal(87, checkedChunks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void GetChunk_Gzip_DecompressesToRaw(int level)
    {
        using OpenSlide osr = OpenBoxes();
        var raw = new OmeZarrGenerator(osr, Options());
        var gzipOptions = Options(compression: OmeZarrCompression.Gzip);
        gzipOptions.GzipLevel = level;
        var gzip = new OmeZarrGenerator(osr, gzipOptions);

        foreach (var (l, c, y, x) in AllChunks(raw))
        {
            byte[] compressed = gzip.GetChunk(l, c, y, x);
            Assert.True(compressed.Length > 2 && compressed[0] == 0x1F && compressed[1] == 0x8B, "gzip magic");
            using var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            Assert.Equal(raw.GetChunk(l, c, y, x), output.ToArray());
        }
    }

    [Theory]
    [InlineData(NgffVersion.V0_5, 92)]
    [InlineData(NgffVersion.V0_4, 93)]
    public void TryGetEntry_ReturnsAllEnumeratedKeys(NgffVersion version, int count)
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options(version));

        var expected = new List<string>();
        if (version == NgffVersion.V0_5)
        {
            expected.Add("zarr.json");
            expected.AddRange(Enumerable.Range(0, 4).Select(l => $"{l}/zarr.json"));
            expected.AddRange(AllChunks(generator).Select(k => $"{k.Level}/c/{k.Channel}/{k.Y}/{k.X}"));
        }
        else
        {
            expected.Add(".zgroup");
            expected.Add(".zattrs");
            expected.AddRange(Enumerable.Range(0, 4).Select(l => $"{l}/.zarray"));
            expected.AddRange(AllChunks(generator).Select(k => $"{k.Level}/{k.Channel}/{k.Y}/{k.X}"));
        }

        List<string> keys = generator.EnumerateKeys().ToList();
        Assert.Equal(count, keys.Count);
        Assert.Equal(expected, keys);
        Assert.Equal(29, BoxesGrid.Sum(g => g.Columns * g.Rows));

        foreach (string key in keys)
        {
            Assert.True(generator.TryGetEntry(key, out byte[]? content), key);
            Assert.NotEmpty(content);
        }

        foreach (var (l, c, y, x) in AllChunks(generator))
        {
            string key = version == NgffVersion.V0_5 ? $"{l}/c/{c}/{y}/{x}" : $"{l}/{c}/{y}/{x}";
            Assert.True(generator.TryGetEntry(key, out byte[]? content));
            Assert.Equal(generator.GetChunk(l, c, y, x), content);
        }
    }

    [Fact]
    public void TryGetEntry_MetadataIsACopy()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options());
        Assert.True(generator.TryGetEntry("zarr.json", out byte[]? first));
        string original = Encoding.UTF8.GetString(first);
        Array.Clear(first);
        Assert.True(generator.TryGetEntry("zarr.json", out byte[]? second));
        Assert.Equal(original, Encoding.UTF8.GetString(second));
    }

    public static IEnumerable<object[]> HostileKeys()
    {
        string[] anyVersion =
        {
            "", "/", "../zarr.json", "0/c/0/0/0/0", "0/c/3/0/0", "0/c/-1/0/0", "0/c/01/0/0", "0/c/0/0/99999999999",
            "9/zarr.json", ".zmetadata", "0\\zarr.json", "zarr.json/", "0//c/0/0/0",
            // More of the same kind.
            "/zarr.json", "./zarr.json", "0/./zarr.json", "00/zarr.json", "+0/zarr.json", " 0/zarr.json", "0/zarr.json ",
            "0/c/0/0/5", "0/c/0/4/0", "4/c/0/0/0", "0/c/0/0", "0/c/0/0/", "0/C/0/0/0", "0/c/0/0/2147483648",
            "0/c/0/0/\u0660", "zarr.json/zarr.json", "ZARR.JSON", "0/zarr.json/0",
        };
        foreach (string key in anyVersion)
        {
            yield return new object[] { NgffVersion.V0_5, key };
            yield return new object[] { NgffVersion.V0_4, key };
        }

        // Keys of the other Zarr version.
        foreach (string key in new[] { "zarr.json", "0/zarr.json", "0/c/0/0/0", "0/3/0/0", "0/.zarray/", "0/.zattrs", "0/.zgroup" })
            yield return new object[] { NgffVersion.V0_4, key };
        foreach (string key in new[] { ".zgroup", ".zattrs", "0/.zarray", "0/0/0/0" })
            yield return new object[] { NgffVersion.V0_5, key };
    }

    [Theory]
    [MemberData(nameof(HostileKeys))]
    public void TryGetEntry_HostileKeys_ReturnFalse(NgffVersion version, string key)
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options(version));
        Assert.False(generator.TryGetEntry(key, out byte[]? content), $"{version}: {key}");
        Assert.Null(content);
    }

    [Fact]
    public void TryGetEntry_NullKey_Throws()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options());
        Assert.Throws<ArgumentNullException>("key", () => generator.TryGetEntry(null!, out _));
    }

    [Fact]
    public void GetChunk_OutOfRange_Throws()
    {
        using OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options());

        Assert.Throws<ArgumentOutOfRangeException>("level", () => generator.GetChunk(-1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("level", () => generator.GetChunk(4, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("channel", () => generator.GetChunk(0, -1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("channel", () => generator.GetChunk(0, 3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("chunkY", () => generator.GetChunk(0, 0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>("chunkY", () => generator.GetChunk(0, 0, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>("chunkX", () => generator.GetChunk(0, 0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>("chunkX", () => generator.GetChunk(0, 0, 0, 5));
        Assert.Throws<ArgumentOutOfRangeException>("chunkY", () => generator.GetChunk(3, 0, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>("chunkX", () => generator.GetChunk(3, 0, 0, 1));
        Assert.Equal(64 * 64, generator.GetChunk(3, 2, 0, 0).Length);
    }

    [Fact]
    public void GetChunk_DisposedSlide_Throws_EvenWhenCached()
    {
        OpenSlide osr = OpenBoxes();
        var generator = new OmeZarrGenerator(osr, Options());
        OpenSlide small = OpenSlide.Open(TestFiles.Get("small.svs"));
        AssociatedImage thumbnail = small.AssociatedImages["thumbnail"];
        var associated = new OmeZarrGenerator(thumbnail, Options(chunkSize: 16));
        generator.GetChunk(0, 0, 0, 0); // the region is now cached
        associated.GetChunk(0, 0, 0, 0);
        osr.Dispose();
        small.Dispose();

        Assert.Throws<ObjectDisposedException>(() => generator.GetChunk(0, 1, 0, 0));
        Assert.Throws<ObjectDisposedException>(() => generator.TryGetEntry("0/c/1/0/0", out _));
        Assert.Throws<ObjectDisposedException>(() => associated.GetChunk(0, 1, 0, 0));
        Assert.Throws<ObjectDisposedException>(() => new OmeZarrGenerator(osr, Options()));
        Assert.Throws<ObjectDisposedException>(() => new OmeZarrGenerator(thumbnail, Options()));

        // Metadata needs no native call and stays available.
        Assert.True(generator.TryGetEntry("zarr.json", out _));
    }

    [Fact]
    public void GetChunk_Parallel_MatchesSequential()
    {
        using OpenSlide osr = OpenBoxes();
        var sequentialGenerator = new OmeZarrGenerator(osr, Options());
        var keys = AllChunks(sequentialGenerator).ToArray();
        Dictionary<(int, int, int, int), byte[]> sequential = keys.ToDictionary(k => k, k => sequentialGenerator.GetChunk(k.Level, k.Channel, k.Y, k.X));

        // A fresh generator with the default 16-entry cache, smaller than the 29 regions, so entries are evicted
        // and re-rendered while other threads use them.
        var options = Options();
        Assert.Equal(16, options.RenderCacheSize);
        var generator = new OmeZarrGenerator(osr, options);
        var failures = new List<string>();
        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 16).Select(seed => new Thread(() =>
        {
            var order = keys.ToArray();
            new Random(seed).Shuffle(order);
            start.Wait();
            foreach (var k in order)
            {
                try
                {
                    if (!generator.GetChunk(k.Level, k.Channel, k.Y, k.X).AsSpan().SequenceEqual(sequential[k]))
                        lock (failures) failures.Add($"{k} differs");
                }
                catch (Exception e)
                {
                    lock (failures) failures.Add($"{k}: {e}");
                }
            }
        })).ToArray();

        foreach (Thread thread in threads)
            thread.Start();
        start.Set();
        foreach (Thread thread in threads)
            thread.Join();

        Assert.Empty(failures);
    }

    // ---- render cache ---------------------------------------------------------------------------------------

    /// <summary>A 300 x 250 one-level source that counts reads and can be told to fail the next few.</summary>
    private sealed class FlakySource : IRenderSource
    {
        private readonly FakeRenderSource _inner = FakeRenderSource.SingleLevel(300, 250);
        private int _failures;

        public int Reads => _inner.ReadCount;

        public void FailNext(int count) => Volatile.Write(ref _failures, count);

        public IReadOnlyList<SlideSize> LevelDimensions => _inner.LevelDimensions;

        public IReadOnlyList<double> LevelDownsamples => _inner.LevelDownsamples;

        public ReadOnlyMemory<byte> ColorProfile => default;

        public int GetBestLevelForDownsample(double downsample) => _inner.GetBestLevelForDownsample(downsample);

        public string? GetProperty(string name) => null;

        public void ReadRegion(long x, long y, int level, int width, int height, Span<byte> destination)
        {
            _inner.ReadRegion(x, y, level, width, height, destination);
            if (Interlocked.Decrement(ref _failures) >= 0)
                throw new InvalidOperationException("read failed");
        }

        public string Describe() => "FlakySource";
    }

    [Fact]
    public void RenderCache_RendersEachRegionOnceForAllChannels()
    {
        var source = new FlakySource();
        var generator = new OmeZarrGenerator(source, "flaky", false, Options());
        for (int channel = 0; channel < 3; channel++)
            generator.GetChunk(0, channel, 0, 0);
        Assert.Equal(1, source.Reads);

        var uncachedSource = new FlakySource();
        var uncachedOptions = Options();
        uncachedOptions.RenderCacheSize = 0;
        var uncached = new OmeZarrGenerator(uncachedSource, "flaky", false, uncachedOptions);
        for (int channel = 0; channel < 3; channel++)
            uncached.GetChunk(0, channel, 0, 0);
        Assert.Equal(3, uncachedSource.Reads);
    }

    [Theory]
    [InlineData(NgffVersion.V0_5)]
    [InlineData(NgffVersion.V0_4)]
    public void RenderCache_ExportInKeyOrder_RendersEachRegionOnce(NgffVersion version)
    {
        // An export walks EnumerateKeys in order. With a single cache entry, that order alone decides whether the
        // three channels of a chunk share one render.
        var source = new FlakySource();
        var options = Options(version);
        options.RenderCacheSize = 1;
        var generator = new OmeZarrGenerator(source, "flaky", false, options);

        foreach (string key in generator.EnumerateKeys())
            Assert.True(generator.TryGetEntry(key, out _), key);

        Assert.Equal(BoxesGrid.Sum(g => g.Columns * g.Rows), source.Reads); // 29 regions, one read each
    }

    [Fact]
    public void RenderCache_EvictsLeastRecentlyUsed()
    {
        var source = new FlakySource();
        var options = Options();
        options.RenderCacheSize = 2;
        var generator = new OmeZarrGenerator(source, "flaky", false, options);

        generator.GetChunk(0, 0, 0, 0); // A
        generator.GetChunk(0, 0, 0, 1); // B
        generator.GetChunk(0, 1, 0, 0); // A again: hit, A becomes most recent
        Assert.Equal(2, source.Reads);
        generator.GetChunk(0, 0, 0, 2); // C evicts B
        generator.GetChunk(0, 2, 0, 0); // A: still cached
        Assert.Equal(3, source.Reads);
        generator.GetChunk(0, 1, 0, 1); // B: evicted, rendered again (evicts C)
        Assert.Equal(4, source.Reads);
    }

    [Fact]
    public void RenderCache_RetriesAfterFailure()
    {
        var source = new FlakySource();
        var generator = new OmeZarrGenerator(source, "flaky", false, Options());

        source.FailNext(1);
        Assert.Throws<InvalidOperationException>(() => generator.GetChunk(0, 0, 0, 0));
        byte[] chunk = generator.GetChunk(0, 0, 0, 0);
        Assert.Equal(2, source.Reads);
        Assert.Equal(64 * 64, chunk.Length);
        generator.GetChunk(0, 1, 0, 0);
        Assert.Equal(2, source.Reads);
    }

    // ---- associated images, options, construction -----------------------------------------------------------

    [Fact]
    public void AssociatedImage_IsSingleLevel()
    {
        using OpenSlide osr = OpenSlide.Open(TestFiles.Get("small.svs"));
        AssociatedImage thumbnail = osr.AssociatedImages["thumbnail"];
        var generator = new OmeZarrGenerator(thumbnail, Options(chunkSize: 16));

        Assert.Equal(1, generator.LevelCount);
        Assert.Equal(new[] { new SlideSize(16, 16) }, generator.LevelDimensions);
        JsonElement multiscale = Multiscale(generator);
        Assert.Equal("thumbnail", multiscale.GetProperty("name").GetString());
        Assert.Equal(1, multiscale.GetProperty("datasets").GetArrayLength());
        Assert.Equal(
            new[] { "zarr.json", "0/zarr.json", "0/c/0/0/0", "0/c/1/0/0", "0/c/2/0/0" }, generator.EnumerateKeys());

        PixelImage tile = new DeepZoomGenerator(thumbnail, 16, 0).GetTile(4, 0, 0);
        byte[] green = generator.GetChunk(0, 1, 0, 0);
        for (int i = 0; i < 16 * 16; i++)
            Assert.Equal(tile.Data[(i * 4) + 1], green[i]);

        // A larger chunk than the image still gives one level.
        Assert.Equal(1, new OmeZarrGenerator(thumbnail).LevelCount);
    }

    [Fact]
    public void Options_AreCopiedAndValidated()
    {
        var defaults = new OmeZarrOptions();
        Assert.Equal(NgffVersion.V0_5, defaults.Version);
        Assert.Equal(512, defaults.ChunkSize);
        Assert.Equal(OmeZarrCompression.None, defaults.Compression);
        Assert.Equal(6, defaults.GzipLevel);
        Assert.False(defaults.LimitBounds);
        Assert.Null(defaults.ColorTransform);
        Assert.Equal(16, defaults.RenderCacheSize);

        var options = new OmeZarrOptions();
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.ChunkSize = 15);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.ChunkSize = 8193);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.GzipLevel = 10);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.GzipLevel = -1);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.RenderCacheSize = -1);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.Version = (NgffVersion)2);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.Compression = (OmeZarrCompression)2);
        Assert.Equal(512, options.ChunkSize);
        options.ChunkSize = 16;
        options.ChunkSize = 8192;
        options.GzipLevel = 0;
        options.GzipLevel = 9;
        options.RenderCacheSize = 0;

        using OpenSlide osr = OpenBoxes();
        var mutable = Options();
        var generator = new OmeZarrGenerator(osr, mutable);
        string before = string.Join("|", generator.EnumerateKeys());
        mutable.ChunkSize = 128;
        mutable.Version = NgffVersion.V0_4;
        mutable.Compression = OmeZarrCompression.Gzip;
        mutable.LimitBounds = true;
        Assert.Equal(64, generator.ChunkSize);
        Assert.Equal(NgffVersion.V0_5, generator.Version);
        Assert.Equal(4, generator.LevelCount);
        Assert.Equal(before, string.Join("|", generator.EnumerateKeys()));
        Assert.Equal(64 * 64, generator.GetChunk(0, 0, 0, 0).Length); // still uncompressed

        var byDefault = new OmeZarrGenerator(osr);
        Assert.Equal(512, byDefault.ChunkSize);
        Assert.Equal(NgffVersion.V0_5, byDefault.Version);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>("slide", () => new OmeZarrGenerator((OpenSlide)null!));
        Assert.Throws<ArgumentNullException>("image", () => new OmeZarrGenerator((AssociatedImage)null!));
    }

    [Fact]
    public void ToString_DescribesSourceAndOptions()
    {
        using OpenSlide osr = OpenBoxes();
        var options = Options(NgffVersion.V0_4, OmeZarrCompression.Gzip);
        options.GzipLevel = 2;
        Assert.Equal(
            $"OmeZarrGenerator({osr}, version: V0_4, chunkSize: 64, compression: Gzip(2), limitBounds: False)",
            new OmeZarrGenerator(osr, options).ToString());
        Assert.Equal(
            $"OmeZarrGenerator({osr}, version: V0_5, chunkSize: 512, compression: None, limitBounds: False)",
            new OmeZarrGenerator(osr).ToString());
    }
}
