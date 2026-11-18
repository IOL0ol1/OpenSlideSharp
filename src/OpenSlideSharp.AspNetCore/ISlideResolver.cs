using System.Diagnostics.CodeAnalysis;

namespace OpenSlideSharp.AspNetCore;

/// <summary>Maps the slide part of a request URL to a slide file.</summary>
/// <remarks>
/// Implementations decide which files may be served, so they must reject anything outside what they intend to
/// expose. Returning <see langword="false"/> makes the endpoint answer 404, the same as for a file that does not
/// exist.
/// </remarks>
public interface ISlideResolver
{
    /// <summary>Resolves the slide part of a URL to a file path.</summary>
    /// <param name="urlPath">
    /// The <c>{slide}</c> part of the URL, after the endpoint prefix: already percent-decoded, with <c>'/'</c>
    /// between segments.
    /// </param>
    /// <param name="filePath">The full path of the slide file, if resolved.</param>
    /// <returns><see langword="true"/> if <paramref name="urlPath"/> names a slide this resolver serves.</returns>
    bool TryResolve(string urlPath, [NotNullWhen(true)] out string? filePath);
}
