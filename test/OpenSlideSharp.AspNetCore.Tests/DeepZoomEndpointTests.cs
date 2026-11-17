using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenSlideSharp.SkiaSharp;
using SkiaSharp;
using Xunit;

namespace OpenSlideSharp.AspNetCore.Tests;

public class DeepZoomEndpointTests
{
    private static Task<TestApp> BoxesApp(Action<OpenSlideServerOptions>? configure = null)
        => TestApp.CreateAsync(new SingleSlideResolver(TestFiles.Get("boxes.tiff"), "boxes"), configure);

    private static (int Width, int Height) DecodedSize(byte[] bytes)
    {
        using SKCodec codec = SKCodec.Create(new SKMemoryStream(bytes))!;
        return (codec.Info.Width, codec.Info.Height);
    }

    private static bool HasIccSegment(byte[] jpeg)
        => jpeg.AsSpan().IndexOf("ICC_PROFILE\0"u8) >= 0;

    [Fact]
    public async Task Dzi_ReturnsXml()
    {
        await using TestApp app = await BoxesApp();
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        string expected = new DeepZoomGenerator(slide, 254, 1, limitBounds: true).GetDzi("jpeg");

        using HttpResponseMessage response = await app.Client.GetAsync("/dz/boxes.dzi", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tile_ReturnsDecodableJpeg()
    {
        await using TestApp app = await BoxesApp();
        using var slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var generator = new DeepZoomGenerator(slide, 254, 1, limitBounds: true);
        SlideSize expected = generator.GetTileDimensions(9, 0, 0);

        using HttpResponseMessage response = await app.Client.GetAsync("/dz/boxes_files/9/0_0.jpeg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(((int)expected.Width, (int)expected.Height), DecodedSize(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Tile_Png_Works()
    {
        await using TestApp app = await BoxesApp();

        using HttpResponseMessage response = await app.Client.GetAsync("/dz/boxes_files/9/0_0.png", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        byte[] body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, body[..4]);
    }

    [Theory]
    [InlineData("/dz/boxes_files/10/0_0.jpeg")]
    [InlineData("/dz/boxes_files/9/5_0.jpeg")]
    [InlineData("/dz/boxes_files/9/0_5.jpeg")]
    [InlineData("/dz/boxes_files/9/0_0.gif")]
    [InlineData("/dz/boxes_files/9/-1_0.jpeg")]
    [InlineData("/dz/boxes_files/9/00_0.jpeg")]
    [InlineData("/dz/boxes_files/9/0_0.JPEG")]
    [InlineData("/dz/boxes_files/9/0/0.jpeg")]
    [InlineData("/dz/boxes_files/9/2147483648_0.jpeg")]
    [InlineData("/dz/boxes_files/x/0_0.jpeg")]
    [InlineData("/dz/boxes_files/0_0.jpeg")]
    [InlineData("/dz/boxes")]
    [InlineData("/dz/")]
    [InlineData("/dz/other.dzi")]
    [InlineData("/dz/other_files/9/0_0.jpeg")]
    public async Task Tile_OutOfRange_Returns404(string url)
    {
        await using TestApp app = await BoxesApp();

        using HttpResponseMessage response = await app.Client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Associated_DziAndTile()
    {
        await using TestApp app = await TestApp.CreateAsync(new SingleSlideResolver(TestFiles.Get("small.svs"), "small"));
        using var slide = OpenSlide.Open(TestFiles.Get("small.svs"));
        var generator = new DeepZoomGenerator(slide.AssociatedImages["thumbnail"], 254, 1);

        using HttpResponseMessage dzi = await app.Client.GetAsync("/dz/small/associated/thumbnail.dzi", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, dzi.StatusCode);
        Assert.Equal(generator.GetDzi("jpeg"), await dzi.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        int top = generator.LevelCount - 1;
        using HttpResponseMessage tile = await app.Client.GetAsync($"/dz/small/associated/thumbnail_files/{top}/0_0.jpeg", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, tile.StatusCode);
        Assert.Equal("image/jpeg", tile.Content.Headers.ContentType?.MediaType);
        SlideSize size = generator.GetTileDimensions(top, 0, 0);
        Assert.Equal(((int)size.Width, (int)size.Height), DecodedSize(await tile.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));

        using HttpResponseMessage missing = await app.Client.GetAsync("/dz/small/associated/nope.dzi", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Dzi_TraversalAttempts_Return404()
    {
        using var directory = new TempDirectory();
        directory.CopyFixture("boxes.tiff", "sub/boxes.tiff");
        directory.CopyFixture("boxes.tiff", "outside.tiff");
        await using TestApp app = await TestApp.CreateAsync(new DirectorySlideResolver(System.IO.Path.Combine(directory.Path, "sub")));

        using (HttpResponseMessage ok = await app.Client.GetAsync("/dz/boxes.tiff.dzi", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        foreach (string url in new[]
        {
            "/dz/..%2Foutside.tiff.dzi",
            "/dz/%2e%2e/outside.tiff.dzi",
            "/dz/sub%5Cboxes.tiff.dzi",
            "/dz/C:/x.dzi",
            "/dz//boxes.tiff.dzi",
        })
        {
            using HttpResponseMessage response = await app.Client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url} gave {(int)response.StatusCode}");
        }

        // HttpClient removes dot segments from a URL, so send these as the raw request path.
        foreach (string path in new[] { "/dz/../outside.tiff.dzi", "/dz/./boxes.tiff.dzi", "/dz/sub/../boxes.tiff.dzi" })
        {
            Assert.Equal(404, await app.GetStatusWithRawPathAsync(path));
        }
    }

    [Fact]
    public async Task Dzi_DirectoryResolverSubfolder_Works()
    {
        using var directory = new TempDirectory();
        directory.CopyFixture("boxes.tiff", "sub/boxes.tiff");
        await using TestApp app = await TestApp.CreateAsync(new DirectorySlideResolver(directory.Path));

        using HttpResponseMessage ok = await app.Client.GetAsync("/dz/sub/boxes.tiff.dzi", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        foreach (string url in new[] { "/dz/..%2Fsub/boxes.tiff.dzi", "/dz/%2e%2e/x.dzi", "/dz/sub%5Cboxes.tiff.dzi", "/dz/C:/x.dzi" })
        {
            using HttpResponseMessage response = await app.Client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url} gave {(int)response.StatusCode}");
        }

        Assert.Equal(404, await app.GetStatusWithRawPathAsync("/dz/sub/../sub/boxes.tiff.dzi"));
    }

    [Fact]
    public async Task Tiles_ConcurrentAcrossEvictions_AllSucceed()
    {
        using var directory = new TempDirectory();
        directory.CopyFixture("boxes.tiff", "boxes.tiff");
        directory.CopyFixture("boxes_0.dcm", "boxes_0.dcm");
        await using TestApp app = await TestApp.CreateAsync(new DirectorySlideResolver(directory.Path), o => o.SlideCacheSize = 1);

        var expected = new Dictionary<string, (int, int)>();
        foreach (string name in new[] { "boxes.tiff", "boxes_0.dcm" })
        {
            using var slide = OpenSlide.Open(TestFiles.Get(name));
            var generator = new DeepZoomGenerator(slide, 254, 1, limitBounds: true);
            int top = generator.LevelCount - 1;
            SlideSize size = generator.GetTileDimensions(top, 0, 0);
            expected[$"/dz/{name}_files/{top}/0_0.jpeg"] = ((int)size.Width, (int)size.Height);
        }

        string[] urls = expected.Keys.ToArray();
        using var gate = new SemaphoreSlim(32);
        Task<string?>[] requests = Enumerable.Range(0, 200).Select(async i =>
        {
            await gate.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                string url = urls[i % 2];
                using HttpResponseMessage response = await app.Client.GetAsync(url, TestContext.Current.CancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return $"{url}: {(int)response.StatusCode}";
                }

                var size = DecodedSize(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                return size == expected[url] ? null : $"{url}: decoded {size}, expected {expected[url]}";
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        string?[] failures = await Task.WhenAll(requests);
        Assert.DoesNotContain(failures, f => f is not null);
    }

    [Fact]
    public async Task ColorMode_Embed_TileCarriesIcc()
    {
        await using TestApp embed = await BoxesApp(o => o.ColorMode = ColorMode.Embed);
        await using TestApp normal = await BoxesApp();

        byte[] embedded = await embed.Client.GetByteArrayAsync("/dz/boxes_files/9/0_0.jpeg", TestContext.Current.CancellationToken);
        byte[] converted = await normal.Client.GetByteArrayAsync("/dz/boxes_files/9/0_0.jpeg", TestContext.Current.CancellationToken);

        Assert.True(HasIccSegment(embedded));
        Assert.False(HasIccSegment(converted));
    }

    [Fact]
    public async Task Head_Tile_ReturnsHeadersWithoutBody()
    {
        await using TestApp app = await BoxesApp();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/dz/boxes_files/9/0_0.jpeg");

        using HttpResponseMessage response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_IsNotAllowed()
    {
        await using TestApp app = await BoxesApp();

        using HttpResponseMessage response = await app.Client.PostAsync("/dz/boxes.dzi", new StringContent(string.Empty, Encoding.UTF8), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
