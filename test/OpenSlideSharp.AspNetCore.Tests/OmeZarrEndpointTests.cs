using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OpenSlideSharp.AspNetCore.Tests;

public class OmeZarrEndpointTests
{
    private static Task<TestApp> BoxesApp(Action<OpenSlideServerOptions>? configure = null)
        => TestApp.CreateAsync(
            new SingleSlideResolver(TestFiles.Get("boxes.tiff"), "boxes"),
            o =>
            {
                o.OmeZarr.ChunkSize = 64;
                configure?.Invoke(o);
            });

    // The generator the server builds for boxes.tiff: the same options, and LimitBounds true.
    private static OmeZarrGenerator ExpectedGenerator(OpenSlide slide, NgffVersion version = NgffVersion.V0_5)
        => new(slide, new OmeZarrOptions { Version = version, ChunkSize = 64, LimitBounds = true });

    private static async Task<HttpResponseMessage> SendAsync(TestApp app, HttpMethod method, string url)
    {
        using var request = new HttpRequestMessage(method, url);
        return await app.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RootMetadata_V05()
    {
        await using TestApp app = await BoxesApp();
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.True(ExpectedGenerator(slide).TryGetEntry("zarr.json", out byte[]? expected));

        using HttpResponseMessage response = await app.Client.GetAsync("/zarr/boxes.zarr/zarr.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Chunk_MatchesGenerator()
    {
        await using TestApp app = await BoxesApp();
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.True(ExpectedGenerator(slide).TryGetEntry("0/c/0/0/0", out byte[]? expected));

        using HttpResponseMessage response = await app.Client.GetAsync("/zarr/boxes.zarr/0/c/0/0/0", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task V04_Keys()
    {
        await using TestApp app = await BoxesApp(o => o.OmeZarr.Version = NgffVersion.V0_4);
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        OmeZarrGenerator expected = ExpectedGenerator(slide, NgffVersion.V0_4);

        foreach (string key in new[] { ".zattrs", ".zgroup", "0/.zarray", "0/0/0/0" })
        {
            using HttpResponseMessage response = await app.Client.GetAsync("/zarr/boxes.zarr/" + key, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{key} gave {(int)response.StatusCode}");
            Assert.True(expected.TryGetEntry(key, out byte[]? bytes));
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            string type = key.Contains(".z", StringComparison.Ordinal) ? "application/json" : "application/octet-stream";
            Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        }

        // The V0_5 keys do not exist in a V0_4 store.
        using HttpResponseMessage other = await app.Client.GetAsync("/zarr/boxes.zarr/zarr.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Theory]
    [InlineData("/zarr/boxes.zarr/.zmetadata")]
    [InlineData("/zarr/boxes.zarr/9/zarr.json")]
    [InlineData("/zarr/boxes.zarr/")]
    [InlineData("/zarr/boxes.zarr")]
    [InlineData("/zarr/other.zarr/zarr.json")]
    [InlineData("/zarr/boxes.zarr/0/c/0/99/0")]
    [InlineData("/zarr/boxes/zarr.json")]
    [InlineData("/zarr/")]
    [InlineData("/zarr/.zarr/zarr.json")]
    [InlineData("/zarr/boxes/associated/.zarr/zarr.json")]
    [InlineData("/zarr/boxes.zarr/0/c/0/0/00")]
    public async Task OmeZarr_UnknownKeys_Return404(string url)
    {
        await using TestApp app = await BoxesApp();

        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using HttpResponseMessage response = await SendAsync(app, method, url);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {url} gave {(int)response.StatusCode}");
            Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        }
    }

    [Fact]
    public async Task OmeZarr_OverlongKey_Returns404()
    {
        await using TestApp app = await BoxesApp();

        using HttpResponseMessage response = await app.Client.GetAsync("/zarr/boxes.zarr/" + new string('0', 300), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OmeZarr_CorsPreflightAndHead()
    {
        await using TestApp app = await BoxesApp();

        using (HttpResponseMessage options = await SendAsync(app, HttpMethod.Options, "/zarr/boxes.zarr/zarr.json"))
        {
            Assert.Equal(HttpStatusCode.NoContent, options.StatusCode);
            Assert.Equal("*", Assert.Single(options.Headers.GetValues("Access-Control-Allow-Origin")));
            string methods = Assert.Single(options.Headers.GetValues("Access-Control-Allow-Methods"));
            Assert.Contains("HEAD", methods, StringComparison.Ordinal);
            Assert.Contains("GET", methods, StringComparison.Ordinal);
        }

        using (HttpResponseMessage head = await SendAsync(app, HttpMethod.Head, "/zarr/boxes.zarr/0/c/0/0/0"))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal("application/octet-stream", head.Content.Headers.ContentType?.MediaType);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal("*", Assert.Single(head.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        using (HttpResponseMessage get = await app.Client.GetAsync("/zarr/boxes.zarr/zarr.json", TestContext.Current.CancellationToken))
        {
            Assert.Equal("*", Assert.Single(get.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        await using TestApp closed = await BoxesApp(o => o.OmeZarr.AllowCrossOrigin = false);
        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Options })
        {
            using HttpResponseMessage response = await SendAsync(closed, method, "/zarr/boxes.zarr/zarr.json");
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"), method.ToString());
            Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"), method.ToString());
        }
    }

    [Fact]
    public async Task OmeZarr_Head_AnswersFromTheKey()
    {
        // HEAD of a chunk that exists answers 200 from the key alone; one outside the grid is 404.
        await using TestApp app = await BoxesApp();

        using HttpResponseMessage inside = await SendAsync(app, HttpMethod.Head, "/zarr/boxes.zarr/3/c/2/0/0");
        using HttpResponseMessage outside = await SendAsync(app, HttpMethod.Head, "/zarr/boxes.zarr/3/c/2/1/0");
        using HttpResponseMessage metadata = await SendAsync(app, HttpMethod.Head, "/zarr/boxes.zarr/0/zarr.json");

        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.Equal("application/json", metadata.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Associated_ThumbnailStore()
    {
        await using TestApp app = await TestApp.CreateAsync(new SingleSlideResolver(TestFiles.Get("small.svs"), "small"));

        using HttpResponseMessage response = await app.Client.GetAsync("/zarr/small/associated/thumbnail.zarr/zarr.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement multiscale = json.RootElement.GetProperty("attributes").GetProperty("ome").GetProperty("multiscales")[0];
        Assert.Equal("thumbnail", multiscale.GetProperty("name").GetString());
    }

    [Fact]
    public async Task UnknownAssociatedName_Returns404()
    {
        await using TestApp app = await TestApp.CreateAsync(new SingleSlideResolver(TestFiles.Get("small.svs"), "small"));

        foreach (string url in new[] { "/zarr/small/associated/nope.zarr/zarr.json", "/dz/small/associated/nope.dzi", "/dz/small/associated/nope_files/0/0_0.jpeg" })
        {
            using HttpResponseMessage response = await app.Client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url} gave {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task OmeZarr_TraversalAttempts_Return404()
    {
        using var directory = new TempDirectory();
        directory.CopyFixture("boxes.tiff", "sub/boxes.tiff");
        directory.CopyFixture("boxes.tiff", "outside.tiff");
        await using TestApp app = await TestApp.CreateAsync(new DirectorySlideResolver(System.IO.Path.Combine(directory.Path, "sub")));

        using (HttpResponseMessage ok = await app.Client.GetAsync("/zarr/boxes.tiff.zarr/zarr.json", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        foreach (string url in new[] { "/zarr/..%2Foutside.tiff.zarr/zarr.json", "/zarr/%2e%2e/outside.tiff.zarr/zarr.json", "/zarr/sub%5Cboxes.tiff.zarr/zarr.json" })
        {
            using HttpResponseMessage response = await app.Client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url} gave {(int)response.StatusCode}");
        }

        Assert.Equal(404, await app.GetStatusWithRawPathAsync("/zarr/../outside.tiff.zarr/zarr.json"));
    }

    [Fact]
    public async Task Post_IsNotAllowed()
    {
        await using TestApp app = await BoxesApp();

        using HttpResponseMessage response = await app.Client.PostAsync("/zarr/boxes.zarr/zarr.json", new StringContent(string.Empty, Encoding.UTF8), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---- sticky OpenSlide errors (Deep Zoom and OME-Zarr alike) -----------------------------------------------

    [Theory]
    [InlineData("/dz/slide_files/{dz}/0_0.jpeg")]
    [InlineData("/zarr/slide.zarr/0/c/0/0/0")]
    public async Task StickyError_Returns500_EvictsAndReopens(string urlTemplate)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "OpenSlide's libjpeg longjmp error path crashes .NET processes on Windows (known issue: see docs/known-issues/windows-cfg-longjmp-crash.md); covered on Linux/macOS CI");
        await using TestApp app = await TestApp.CreateAsync(new SingleSlideResolver(TestFiles.Get("unreadable.svs"), "slide"));
        var cache = app.Services.GetRequiredService<SlideCache>();

        int top;
        using (var slide = OpenSlide.Open(TestFiles.Get("unreadable.svs")))
        {
            top = new DeepZoomGenerator(slide, 254, 1, limitBounds: true).LevelCount - 1;
        }

        string url = urlTemplate.Replace("{dz}", top.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        using (HttpResponseMessage first = await app.Client.GetAsync(url, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        }

        Assert.Equal(0, cache.Count);
        int opens = cache.OpenCount;

        using (HttpResponseMessage second = await app.Client.GetAsync(url, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, second.StatusCode);
        }

        Assert.Equal(opens + 1, cache.OpenCount);
        Assert.Equal(0, cache.Count);
    }
}
