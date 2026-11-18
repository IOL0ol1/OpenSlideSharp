using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenSlideSharp.AspNetCore;

namespace OpenSlideSharp.Web;

/// <summary>One entry of the slide listing: a directory with children, or a slide with a URL path.</summary>
public sealed record SlideNode(string Name, string? UrlPath, IReadOnlyList<SlideNode> Children);

/// <summary>What the slide page shows about one slide.</summary>
public sealed record SlideView(
    string Name,
    string DeepZoomUrl,
    string Mpp,
    IReadOnlyList<KeyValuePair<string, string>> Associated,
    IReadOnlyList<KeyValuePair<string, string>> Properties,
    string OmeZarrUrl,
    string VizarrUrl);

/// <summary>The slide or slide directory this app serves, and what the pages show about it.</summary>
public sealed class SlideSite
{
    /// <summary>The fixed <c>{slide}</c> name in single-slide mode.</summary>
    public const string SingleName = "slide";

    private const int MaxListingDepth = 32;
    private static readonly TimeSpan ListingLifetime = TimeSpan.FromSeconds(60);

    private readonly SlideCache _cache;
    private readonly string? _root;
    private readonly object _listingLock = new();
    private IReadOnlyList<SlideNode>? _listing;
    private DateTimeOffset _listingTime;

    /// <summary>Resolves <c>SlidePath</c> from the configuration: a file gives single-slide mode, a directory directory mode.</summary>
    public SlideSite(IConfiguration configuration, SlideCache cache)
    {
        _cache = cache;
        string? path = configuration["SlidePath"];
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("Set SlidePath (the first command-line argument) to a slide file or a directory of slides.");
        }

        path = Path.GetFullPath(path);
        if (File.Exists(path))
        {
            Resolver = new SingleSlideResolver(path, SingleName);
        }
        else if (Directory.Exists(path))
        {
            Resolver = new DirectorySlideResolver(path);
            _root = path;
        }
        else
        {
            throw new FileNotFoundException($"SlidePath '{path}' is neither a file nor a directory.");
        }
    }

    /// <summary>The resolver for the Deep Zoom and OME-Zarr endpoints.</summary>
    public ISlideResolver Resolver { get; }

    /// <summary>True when one slide is served, under the name <see cref="SingleName"/>.</summary>
    public bool IsSingle => _root is null;

    /// <summary>The slides below the directory, as a tree. Cached for 60 seconds.</summary>
    public IReadOnlyList<SlideNode> GetListing()
    {
        if (_root is null)
        {
            return [];
        }

        lock (_listingLock)
        {
            if (_listing is null || DateTimeOffset.UtcNow - _listingTime > ListingLifetime)
            {
                _listing = Scan(_root, string.Empty, 0);
                _listingTime = DateTimeOffset.UtcNow;
            }

            return _listing;
        }
    }

    /// <summary>Describes a slide for the slide page, or returns null if it is not served or cannot be opened.</summary>
    public SlideView? Describe(string name, HttpRequest request)
    {
        if (!Resolver.TryResolve(name, out string? file))
        {
            return null;
        }

        using SlideLease? lease = _cache.TryAcquire(file);
        if (lease is null)
        {
            return null;
        }

        OpenSlide slide = lease.Entry.Slide;
        string pathBase = request.PathBase.Value ?? string.Empty;
        string escaped = EscapePath(name);

        var associated = slide.AssociatedImages.Keys
            .Order(StringComparer.Ordinal)
            .Select(n => new KeyValuePair<string, string>(n, $"{pathBase}/dz/{escaped}/associated/{Uri.EscapeDataString(n)}.dzi"))
            .ToList();
        var properties = slide.Properties
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        string zarr = $"{request.Scheme}://{request.Host}{pathBase}/zarr/{escaped}.zarr/";
        return new SlideView(
            name,
            $"{pathBase}/dz/{escaped}.dzi",
            FormatMpp(slide.Properties),
            associated,
            properties,
            zarr,
            "https://hms-dbmi.github.io/vizarr/?source=" + Uri.EscapeDataString(zarr));
    }

    /// <summary>Escapes each segment of a <c>'/'</c> separated slide name for use in a URL.</summary>
    public static string EscapePath(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    // The mean of the horizontal and vertical micrometers per pixel, or "0" when either is missing, as in the
    // Python examples.
    private static string FormatMpp(IReadOnlyDictionary<string, string> properties)
    {
        if (properties.TryGetValue(OpenSlideProperties.MppX, out string? x)
            && properties.TryGetValue(OpenSlideProperties.MppY, out string? y)
            && double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double mppX)
            && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double mppY))
        {
            double mpp = (mppX + mppY) / 2;
            if (double.IsFinite(mpp) && mpp > 0)
            {
                return mpp.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        return "0";
    }

    private IReadOnlyList<SlideNode> Scan(string directory, string urlPrefix, int depth)
    {
        var nodes = new List<SlideNode>();
        if (depth > MaxListingDepth)
        {
            return nodes;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return nodes;
        }

        foreach (FileSystemInfo entry in entries)
        {
            string urlPath = urlPrefix + entry.Name;
            if (entry is DirectoryInfo)
            {
                // Links to directories are not followed: they could lead outside the root or loop.
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                IReadOnlyList<SlideNode> children = Scan(entry.FullName, urlPath + "/", depth + 1);
                if (children.Count > 0)
                {
                    nodes.Add(new SlideNode(entry.Name, null, children));
                }
            }
            else if (Resolver.TryResolve(urlPath, out string? file) && OpenSlide.DetectFormat(file) is not null)
            {
                nodes.Add(new SlideNode(entry.Name, urlPath, []));
            }
        }

        return nodes;
    }
}
