using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace OpenSlideSharp.Web.Tests;

public sealed class WebSampleTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static WebApplicationFactory<Program> CreateFactory(string slidePath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("SlidePath", slidePath));

    [Fact]
    public async Task SingleMode_IndexShowsViewer()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(Fixture("boxes.tiff"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/dz/slide.dzi", html);
        Assert.Contains("openseadragon", html);
    }

    [Fact]
    public async Task DirectoryMode_ListsSlides()
    {
        string root = Path.Combine(Path.GetTempPath(), "openslidesharp-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a"));
        try
        {
            File.Copy(Fixture("boxes.tiff"), Path.Combine(root, "a", "boxes.tiff"));
            File.WriteAllText(Path.Combine(root, "notes.txt"), "not a slide");

            await using WebApplicationFactory<Program> factory = CreateFactory(root);
            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);
            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("a/boxes.tiff", html);
            Assert.DoesNotContain("notes.txt", html);

            // The slide page of a listed slide works in directory mode too.
            using HttpResponseMessage slide = await client.GetAsync("/slide/a/boxes.tiff", TestContext.Current.CancellationToken);
            string slideHtml = await slide.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, slide.StatusCode);
            Assert.Contains("/dz/a/boxes.tiff.dzi", slideHtml);

            using HttpResponseMessage missing = await client.GetAsync("/slide/notes.txt", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SlidePage_ShowsPropertiesAssociatedAndOmeZarr()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(Fixture("small.svs"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("openslide.vendor", html);
        Assert.Contains("thumbnail", html);
        Assert.Contains("/zarr/slide.zarr/", html);
        Assert.Contains("vizarr", html);
    }

    [Fact]
    public async Task StaticAssets_Served()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(Fixture("boxes.tiff"));
        using HttpClient client = factory.CreateClient();

        foreach (string path in new[] { "/lib/openseadragon.min.js", "/lib/openseadragon-scalebar.js", "/lib/images/home_rest.png", "/viewer/viewer.js", "/viewer/viewer.css" })
        {
            using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Endpoints_ServeDeepZoomAndOmeZarr()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(Fixture("boxes.tiff"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage dzi = await client.GetAsync("/dz/slide.dzi", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, dzi.StatusCode);
        using HttpResponseMessage zarr = await client.GetAsync("/zarr/slide.zarr/zarr.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, zarr.StatusCode);
    }

    [Fact]
    public void CommandLine_MapsPythonOptions()
    {
        IConfiguration configuration = Program.BuildConfiguration(
            ["-s", "510", "-e", "2", "-f", "png", "-Q", "90", "--color-mode", "ignore", "-B", "x.svs"]);

        Assert.Equal("510", configuration["OpenSlide:DeepZoom:TileSize"]);
        Assert.Equal("2", configuration["OpenSlide:DeepZoom:Overlap"]);
        Assert.Equal("png", configuration["OpenSlide:DeepZoom:Format"]);
        Assert.Equal("90", configuration["OpenSlide:DeepZoom:Quality"]);
        Assert.Equal("ignore", configuration["OpenSlide:ColorMode"]);
        Assert.Equal("false", configuration["OpenSlide:DeepZoom:LimitBounds"]);
        Assert.Equal("false", configuration["OpenSlide:OmeZarr:LimitBounds"]);
        Assert.Equal("x.svs", configuration["SlidePath"]);
    }

    [Fact]
    public void CommandLine_ListenAndPortFormUrls()
    {
        IConfiguration configuration = Program.BuildConfiguration(["--listen", "0.0.0.0", "-p", "8080", "slides"]);

        Assert.Equal("http://0.0.0.0:8080", configuration["urls"]);
        Assert.Equal("slides", configuration["SlidePath"]);
        Assert.Null(configuration["OpenSlide:DeepZoom:LimitBounds"]);

        Assert.Equal("http://127.0.0.1:9000", Program.BuildConfiguration(["-p", "9000"])["urls"]);
        Assert.Null(Program.BuildConfiguration([])["urls"]);
    }

    [Fact]
    public void AppSettings_LeaveTheUrlsToTheEnvironment()
    {
        // A "Urls" in appsettings.json would override ASPNETCORE_URLS and ASPNETCORE_HTTP_PORTS. (The sample's own
        // default, http://127.0.0.1:5000, applies only when nothing sets one: see DefaultUrls.)
        IConfiguration settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();

        Assert.Equal("10", settings["OpenSlide:SlideCacheSize"]);
        Assert.Null(settings["Urls"]);
    }

    [Theory]
    [InlineData(null, null, "http://127.0.0.1:5000")]
    [InlineData("urls", "http://0.0.0.0:8080", null)]          // ASPNETCORE_URLS, --urls, -l/-p
    [InlineData("http_ports", "8080", null)]                    // ASPNETCORE_HTTP_PORTS
    [InlineData("https_ports", "8443", null)]                   // ASPNETCORE_HTTPS_PORTS
    [InlineData("Kestrel:Endpoints:Http:Url", "http://*:80", null)]
    public void DefaultUrls_OnlyWhenNothingElseSetsThem(string? key, string? value, string? expected)
    {
        var settings = new Dictionary<string, string?>();
        if (key is not null)
        {
            settings[key] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        Assert.Equal(expected, Program.DefaultUrls(configuration));
    }

    [Fact]
    public void Run_InvalidOptions_PrintMessageAndUsage_Return2()
    {
        var stderr = new StringWriter();

        int code = Program.Run(["--OpenSlide:SlideCacheSize=0", "-s", "0", Fixture("boxes.tiff")], stderr);

        Assert.Equal(2, code);
        Assert.Contains("SlideCacheSize must be at least 1", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("DeepZoom.TileSize must be at least 1", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: OpenSlideSharp.Web", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_BadArguments_Return2()
    {
        var missing = new StringWriter();
        Assert.Equal(2, Program.Run([], missing));
        Assert.Contains("SlidePath", missing.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: OpenSlideSharp.Web", missing.ToString(), StringComparison.Ordinal);

        var unknown = new StringWriter();
        Assert.Equal(2, Program.Run(["--no-such-option", "x"], unknown));
        Assert.Contains("Unknown option '--no-such-option'", unknown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLine_RejectsBadArguments()
    {
        Assert.Throws<ArgumentException>(() => Program.BuildConfiguration(["-s"]));
        Assert.Throws<ArgumentException>(() => Program.BuildConfiguration(["--no-such-option", "x"]));
        Assert.Throws<ArgumentException>(() => Program.BuildConfiguration(["a.svs", "b.svs"]));
    }
}
