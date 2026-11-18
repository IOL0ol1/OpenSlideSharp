using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSlideSharp.AspNetCore.Internal;
using OpenSlideSharp.SkiaSharp;

namespace OpenSlideSharp.AspNetCore;

/// <summary>Maps the Deep Zoom endpoints.</summary>
public static class DeepZoomEndpointRouteBuilderExtensions
{
    /// <summary>Serves slides from <paramref name="resolver"/> as Deep Zoom images, for GET and HEAD requests.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The URL prefix, for example <c>/dz</c>.</param>
    /// <param name="resolver">Turns the <c>{slide}</c> part of a URL into a file.</param>
    /// <returns>A convention builder for the endpoint.</returns>
    /// <remarks>
    /// <para>The URLs under <paramref name="prefix"/>, for a slide named <c>{slide}</c>:</para>
    /// <list type="bullet">
    /// <item><c>{slide}.dzi</c>: the DZI document (<c>application/xml</c>).</item>
    /// <item><c>{slide}_files/{level}/{col}_{row}.{jpeg|png}</c>: a tile (<c>image/jpeg</c> or <c>image/png</c>).</item>
    /// <item><c>{slide}/associated/{name}.dzi</c> and <c>{slide}/associated/{name}_files/...</c>: the same for an associated image.</item>
    /// </list>
    /// <para>
    /// Everything else, including a slide that cannot be opened and a tile outside the pyramid, is a 404. A slide in
    /// the OpenSlide error state gives a 500 and is evicted from the <see cref="SlideCache"/>, so the next request
    /// opens it again. The services from <c>AddOpenSlide</c> must be registered.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><c>AddOpenSlide</c> was not called.</exception>
    public static IEndpointConventionBuilder MapDeepZoom(this IEndpointRouteBuilder endpoints, string prefix, ISlideResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(resolver);

        ILogger logger = EndpointSupport.Prepare(endpoints, nameof(MapDeepZoom), "OpenSlideSharp.AspNetCore.DeepZoom");
        string pattern = prefix.TrimEnd('/') + "/{**path}";
        return endpoints.MapMethods(
            pattern,
            [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, SlideCache cache, IOptions<OpenSlideServerOptions> options) =>
                Handle(context, cache, options.Value, resolver, logger));
    }

    private static IResult Handle(HttpContext context, SlideCache cache, OpenSlideServerOptions options, ISlideResolver resolver, ILogger logger)
    {
        string path = context.Request.RouteValues["path"] as string ?? string.Empty;
        if (!DeepZoomRequest.TryParse(path, out DeepZoomRequest request)
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
            SlideEntry entry = lease.Entry;
            if (!EndpointSupport.TryGetGenerator(entry.GetDeepZoom, request.AssociatedName, out DeepZoomGenerator generator))
            {
                return Results.NotFound(); // no such associated image
            }

            if (!request.IsTile)
            {
                string format = options.DeepZoom.Format == ImageFormat.Png ? "png" : "jpeg";
                return Results.Text(generator.GetDzi(format), "application/xml");
            }

            if (request.Level >= generator.LevelCount
                || request.Column >= generator.LevelTiles[request.Level].Columns
                || request.Row >= generator.LevelTiles[request.Level].Rows)
            {
                return Results.NotFound();
            }

            ImageFormat imageFormat = request.Format == "png" ? ImageFormat.Png : ImageFormat.Jpeg;
            string contentType = request.Format == "png" ? "image/png" : "image/jpeg";
            if (HttpMethods.IsHead(context.Request.Method))
            {
                return new EndpointSupport.HeadResult(contentType);
            }

            PixelImage tile = generator.GetTile(request.Level, request.Column, request.Row);
            byte[] encoded = tile.Encode(imageFormat, options.DeepZoom.Quality, entry.EncodeMode(request.AssociatedName));
            return Results.Bytes(encoded, contentType);
        }
        catch (OpenSlideException e)
        {
            return EndpointSupport.SlideError(logger, cache, lease, path, e);
        }
    }
}
