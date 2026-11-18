#if SMOKE_ASPNETCORE
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSlideSharp.AspNetCore;

/// <summary>
/// Serves the slide with OpenSlideSharp.AspNetCore on a free local port, then fetches the DZI, a tile and the
/// OME-Zarr root metadata over HTTP.
/// </summary>
internal static class AspNetCoreSmoke
{
    public static async Task<int> RunAsync(string slidePath)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddOpenSlide();
        await using WebApplication app = builder.Build();
        var resolver = new SingleSlideResolver(slidePath, name: "slide");
        app.MapDeepZoom("/dz", resolver);
        app.MapOmeZarr("/zarr", resolver);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            string? failed =
                await Check(http, "/dz/slide.dzi", "application/xml", body => body.AsSpan().IndexOf("<Image"u8) >= 0)
                ?? await Check(http, "/dz/slide_files/0/0_0.jpeg", "image/jpeg", body => body.Length > 2 && body[0] == 0xFF && body[1] == 0xD8)
                ?? await Check(http, "/zarr/slide.zarr/zarr.json", "application/json", body => body.AsSpan().IndexOf("\"multiscales\""u8) >= 0);
            if (failed is not null)
            {
                Console.WriteLine("aspnetcore=bad " + failed);
                return 5;
            }

            Console.WriteLine("aspnetcore=ok");
            return 0;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    // Returns the URL when the response is not a 200 of the expected type and content, otherwise null.
    private static async Task<string?> Check(HttpClient http, string url, string contentType, Func<byte[], bool> isValid)
    {
        using HttpResponseMessage response = await http.GetAsync(url);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        string? type = response.Content.Headers.ContentType?.MediaType;
        Console.WriteLine($"GET {url}: {(int)response.StatusCode} {type}, {body.Length} bytes");
        return response.StatusCode == HttpStatusCode.OK && type == contentType && isValid(body) ? null : url;
    }
}
#endif
