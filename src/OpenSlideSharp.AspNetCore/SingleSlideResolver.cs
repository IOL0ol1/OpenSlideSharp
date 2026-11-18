using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using OpenSlideSharp.AspNetCore.Internal;

namespace OpenSlideSharp.AspNetCore;

/// <summary>Serves one slide file under a fixed name, as openslide-python's <c>deepzoom_server.py</c> does.</summary>
/// <remarks>
/// With the name <c>slide</c>, the Deep Zoom image is at <c>{prefix}/slide.dzi</c>. Only that exact name
/// (compared case-sensitively) resolves.
/// </remarks>
public sealed class SingleSlideResolver : ISlideResolver
{
    private readonly string _filePath;

    /// <summary>Creates a resolver for one slide file.</summary>
    /// <param name="filePath">Path of the slide file. It is made absolute, but not checked for existence.</param>
    /// <param name="name">
    /// The <c>{slide}</c> part of the URLs. It may contain <c>'/'</c> between segments, but no empty segment,
    /// <c>.</c>, <c>..</c>, backslash, colon or control character.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty or not a valid path, or <paramref name="name"/> is not a valid URL path.</exception>
    public SingleSlideResolver(string filePath, string name = "slide")
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(name);
        if (UrlPath.SplitSafe(name) is null)
        {
            throw new ArgumentException($"'{name}' is not a valid slide name for a URL.", nameof(name));
        }

        _filePath = Path.GetFullPath(filePath);
        Name = name;
    }

    /// <summary>Gets the <c>{slide}</c> part of the URLs.</summary>
    public string Name { get; }

    /// <inheritdoc/>
    public bool TryResolve(string urlPath, [NotNullWhen(true)] out string? filePath)
    {
        if (string.Equals(urlPath, Name, StringComparison.Ordinal))
        {
            filePath = _filePath;
            return true;
        }

        filePath = null;
        return false;
    }
}
