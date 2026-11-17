using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSlideSharp.AspNetCore.Internal;

namespace OpenSlideSharp.AspNetCore;

/// <summary>Maps the OME-Zarr endpoints.</summary>
public static class OmeZarrEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Serves slides from <paramref name="resolver"/> as OME-Zarr stores, for GET, HEAD and OPTIONS requests.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The URL prefix, for example <c>/zarr</c>.</param>
    /// <param name="resolver">Turns the <c>{slide}</c> part of a URL into a file.</param>
    /// <returns>A convention builder for the endpoint.</returns>
    /// <remarks>
    /// <para>The URLs under <paramref name="prefix"/>, for a slide named <c>{slide}</c>:</para>
    /// <list type="bullet">
    /// <item><c>{slide}.zarr/{key}</c>: an entry of the store, with the keys <see cref="OmeZarrGenerator.EnumerateKeys"/> lists.
    /// Metadata documents are <c>application/json</c>, chunks <c>application/octet-stream</c>.</item>
    /// <item><c>{slide}/associated/{name}.zarr/{key}</c>: the same for an associated image.</item>
    /// </list>
    /// <para>
    /// GET renders a chunk when asked for one. HEAD answers 200 with the Content-Type and no body for a key that
    /// exists, without rendering. OPTIONS answers 204 with the CORS preflight headers. When
    /// <see cref="OmeZarrServerOptions.AllowCrossOrigin"/> is set (the default), every response of this endpoint,
    /// including 404 and 500, carries <c>Access-Control-Allow-Origin: *</c>.
    /// </para>
    /// <para>
    /// Everything else, including probes such as <c>.zmetadata</c>, a key longer than
    /// <see cref="OmeZarrGenerator.MaxKeyLength"/> and a slide that cannot be opened, is a 404. A slide in the
    /// OpenSlide error state gives a 500 and is evicted from the <see cref="SlideCache"/>, so the next request
    /// opens it again. The services from <c>AddOpenSlide</c> must be registered.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IEndpointConventionBuilder MapOmeZarr(this IEndpointRouteBuilder endpoints, string prefix, ISlideResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(resolver);

        ILogger logger = EndpointSupport.CreateLogger(endpoints, "OpenSlideSharp.AspNetCore.OmeZarr");
        string pattern = prefix.TrimEnd('/') + "/{**path}";
        return endpoints.MapMethods(
            pattern,
            [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options],
            (HttpContext context, SlideCache cache, IOptions<OpenSlideServerOptions> options) =>
                Handle(context, cache, options.Value, resolver, logger));
    }

    private static IResult Handle(HttpContext context, SlideCache cache, OpenSlideServerOptions options, ISlideResolver resolver, ILogger logger)
    {
        bool preflight = HttpMethods.IsOptions(context.Request.Method);
        if (options.OmeZarr.AllowCrossOrigin)
        {
            context.Response.Headers.AccessControlAllowOrigin = "*";
            if (preflight)
            {
                context.Response.Headers.AccessControlAllowMethods = "GET, HEAD, OPTIONS";
                context.Response.Headers.AccessControlMaxAge = "86400";
            }
        }

        if (preflight)
        {
            return Results.NoContent();
        }

        string path = context.Request.RouteValues["path"] as string ?? string.Empty;
        if (!OmeZarrRequest.TryParse(path, out OmeZarrRequest request)
            || !resolver.TryResolve(request.Slide, out string? filePath))
        {
            return Results.NotFound();
        }

        using SlideLease? lease = cache.TryAcquire(filePath);
        if (lease is null)
        {
            return Results.NotFound();
        }

        try
        {
            if (!EndpointSupport.TryGetGenerator(lease.Entry.GetOmeZarr, request.AssociatedName, out OmeZarrGenerator generator))
            {
                return Results.NotFound(); // no such associated image
            }

            if (HttpMethods.IsHead(context.Request.Method))
            {
                return generator.ContainsKey(request.Key)
                    ? new EndpointSupport.HeadResult(ContentType(request.Key))
                    : Results.NotFound();
            }

            return generator.TryGetEntry(request.Key, out byte[]? content)
                ? Results.Bytes(content, ContentType(request.Key))
                : Results.NotFound();
        }
        catch (OpenSlideException e)
        {
            return EndpointSupport.SlideError(logger, cache, filePath, path, e);
        }
    }

    // Metadata documents are JSON; a key that exists and is not one of them is a chunk.
    private static string ContentType(string key)
        => key is "zarr.json" or ".zattrs" or ".zgroup"
            || key.EndsWith("/zarr.json", StringComparison.Ordinal)
            || key.EndsWith("/.zarray", StringComparison.Ordinal)
            ? "application/json"
            : "application/octet-stream";
}
