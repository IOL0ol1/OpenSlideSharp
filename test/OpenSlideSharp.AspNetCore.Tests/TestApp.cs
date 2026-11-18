using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenSlideSharp.AspNetCore.Tests;

/// <summary>A minimal in-memory web app with the OpenSlide endpoints mapped, and a client for it.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>Starts an app with <c>AddOpenSlide</c>, the Deep Zoom endpoints at <c>/dz</c> and the OME-Zarr endpoints at <c>/zarr</c>.</summary>
    public static async Task<TestApp> CreateAsync(ISlideResolver resolver, Action<OpenSlideServerOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddOpenSlide(configure);

        WebApplication app = builder.Build();
        app.MapDeepZoom("/dz", resolver);
        app.MapOmeZarr("/zarr", resolver);
        await app.StartAsync();
        return new TestApp(app, app.GetTestClient());
    }

    /// <summary>
    /// Sends a GET with the request path set as given, bypassing the dot-segment removal that <see cref="HttpClient"/>
    /// does to a URL, as a client that sends the raw request line would.
    /// </summary>
    public async Task<int> GetStatusWithRawPathAsync(string path)
    {
        HttpContext context = await _app.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = new PathString(path);
        });
        return context.Response.StatusCode;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
