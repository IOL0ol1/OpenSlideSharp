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
    private const string Associated = "/associated/";
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

        string slide = slidePart;
        string? associatedName = null;
        int associated = slidePart.LastIndexOf(Associated, StringComparison.Ordinal);
        if (associated >= 0)
        {
            slide = slidePart[..associated];
            associatedName = slidePart[(associated + Associated.Length)..];
            if (associatedName.Length == 0 || associatedName.Contains('/'))
            {
                return false;
            }
        }

        if (slide.Length == 0)
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
