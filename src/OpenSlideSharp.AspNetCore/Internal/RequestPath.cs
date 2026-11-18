using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OpenSlideSharp.AspNetCore.Internal;

/// <summary>What a Deep Zoom request path asks for: a DZI document or one tile, of a slide or an associated image.</summary>
internal readonly record struct DeepZoomRequest(
    string Slide,
    string? AssociatedName,
    bool IsTile,
    int Level,
    int Column,
    int Row,
    string Format)
{
    private const string FilesSuffix = "_files/";
    private const string DziSuffix = ".dzi";

    /// <summary>
    /// Parses the part of the URL after the endpoint prefix, without a leading <c>'/'</c>. Returns
    /// <see langword="false"/> for every other form, which the endpoint answers with 404.
    /// </summary>
    public static bool TryParse(string path, out DeepZoomRequest request)
    {
        request = default;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string slidePart;
        bool isTile = false;
        int level = 0, column = 0, row = 0;
        string format = string.Empty;

        if (path.EndsWith(DziSuffix, StringComparison.Ordinal))
        {
            slidePart = path[..^DziSuffix.Length];
        }
        else
        {
            int files = path.LastIndexOf(FilesSuffix, StringComparison.Ordinal);
            if (files < 0 || !TryParseTile(path[(files + FilesSuffix.Length)..], out level, out column, out row, out format))
            {
                return false;
            }

            isTile = true;
            slidePart = path[..files];
        }

        if (!SlidePath.TrySplit(slidePart, out string slide, out string? associatedName))
        {
            return false;
        }

        request = new DeepZoomRequest(slide, associatedName, isTile, level, column, row, format);
        return true;
    }

    // {level}/{col}_{row}.{fmt}
    private static bool TryParseTile(string tail, out int level, out int column, out int row, out string format)
    {
        level = column = row = 0;
        format = string.Empty;

        int slash = tail.IndexOf('/');
        if (slash < 0 || !TryParseIndex(tail.AsSpan(0, slash), out level))
        {
            return false;
        }

        ReadOnlySpan<char> file = tail.AsSpan(slash + 1);
        int dot = file.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        string extension = file[(dot + 1)..].ToString();
        if (extension != "jpeg" && extension != "png")
        {
            return false;
        }

        ReadOnlySpan<char> stem = file[..dot];
        int underscore = stem.IndexOf('_');
        if (underscore < 0 || !TryParseIndex(stem[..underscore], out column) || !TryParseIndex(stem[(underscore + 1)..], out row))
        {
            return false;
        }

        format = extension;
        return true;
    }

    // A non-negative decimal number in int range, without a sign, whitespace or leading zeros.
    private static bool TryParseIndex(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.IsEmpty || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>What an OME-Zarr request path asks for: one key of the store of a slide or an associated image.</summary>
internal readonly record struct OmeZarrRequest(string Slide, string? AssociatedName, string Key)
{
    private const string StoreMarker = ".zarr/";

    /// <summary>
    /// Parses the part of the URL after the endpoint prefix, without a leading <c>'/'</c>: the slide part, then
    /// <c>.zarr/</c>, then the key. The store ends at the last <c>.zarr/</c>, since no key contains one while a
    /// slide path may (a slide in a directory named <c>x.zarr</c>). A path that ends at <c>.zarr</c> has no key.
    /// Returns <see langword="false"/> for every other form, and for a key longer than
    /// <see cref="OmeZarrGenerator.MaxKeyLength"/>; the endpoint answers all of them with 404.
    /// </summary>
    public static bool TryParse(string path, out OmeZarrRequest request)
    {
        request = default;
        int marker = string.IsNullOrEmpty(path) ? -1 : path.LastIndexOf(StoreMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        string key = path[(marker + StoreMarker.Length)..];
        if (key.Length == 0 || key.Length > OmeZarrGenerator.MaxKeyLength
            || !SlidePath.TrySplit(path[..marker], out string slide, out string? associatedName))
        {
            return false;
        }

        request = new OmeZarrRequest(slide, associatedName, key);
        return true;
    }
}

internal static class SlidePath
{
    private const string Associated = "/associated/";

    /// <summary>
    /// Splits <c>{slide}</c> or <c>{slide}/associated/{name}</c> into the slide and the associated image name
    /// (<see langword="null"/> for the slide itself). Returns <see langword="false"/> if either part is empty or the
    /// name has a <c>'/'</c>.
    /// </summary>
    public static bool TrySplit(string part, out string slide, out string? associatedName)
    {
        slide = part;
        associatedName = null;
        int associated = part.LastIndexOf(Associated, StringComparison.Ordinal);
        if (associated >= 0)
        {
            slide = part[..associated];
            associatedName = part[(associated + Associated.Length)..];
            if (associatedName.Length == 0 || associatedName.Contains('/'))
            {
                return false;
            }
        }

        return slide.Length > 0;
    }
}
