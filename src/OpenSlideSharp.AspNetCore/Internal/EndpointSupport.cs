using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenSlideSharp.AspNetCore.Internal;

/// <summary>What the Deep Zoom and OME-Zarr endpoints have in common.</summary>
internal static class EndpointSupport
{
    /// <summary>Creates the logger of an endpoint group once, when it is mapped.</summary>
    public static ILogger CreateLogger(IEndpointRouteBuilder endpoints, string category)
        => endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(category);

    /// <summary>
    /// Gets the generator of an image through <paramref name="get"/>. Only the lookup of an associated image name
    /// may fail with <see cref="KeyNotFoundException"/>, and that is a missing image, not a fault:
    /// <see langword="false"/> is returned and the caller answers 404. Any other exception goes to the caller.
    /// </summary>
    public static bool TryGetGenerator<T>(Func<string?, T> get, string? associatedName, out T generator)
        where T : class
    {
        try
        {
            generator = get(associatedName);
            return true;
        }
        catch (KeyNotFoundException)
        {
            generator = null!;
            return false;
        }
    }

    /// <summary>
    /// Answers a request during which OpenSlide reported an error: the slide is in the sticky error state, so the
    /// instance the request used is evicted (the next request opens it again, and a newer instance another request
    /// opened meanwhile stays) and the response is a 500.
    /// </summary>
    public static IResult SlideError(ILogger logger, SlideCache cache, SlideLease lease, string request, OpenSlideException exception)
    {
        Log.SlideError(logger, lease.Entry.FilePath, request, exception);
        cache.Evict(lease);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    /// <summary>A response with the status and Content-Type of a GET and no body: the answer to HEAD.</summary>
    public sealed class HeadResult(string contentType) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = contentType;
            return Task.CompletedTask;
        }
    }
}
