namespace OpenSlideSharp.AspNetCore.Internal;

/// <summary>Rules for the <c>{slide}</c> part of a URL, which resolvers turn into file paths.</summary>
internal static class UrlPath
{
    /// <summary>
    /// Splits a <c>'/'</c>-separated relative path into its segments, or returns <see langword="null"/> if the
    /// path is empty or any segment is unsafe (see <see cref="IsSafeSegment"/>). A leading, trailing or doubled
    /// <c>'/'</c> makes an empty segment, so absolute paths are rejected too.
    /// </summary>
    public static string[]? SplitSafe(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string[] segments = path.Split('/');
        foreach (string segment in segments)
        {
            if (!IsSafeSegment(segment))
            {
                return null;
            }
        }

        return segments;
    }

    /// <summary>
    /// Returns <see langword="false"/> for an empty segment, <c>.</c>, <c>..</c>, and any segment with a backslash,
    /// a colon (drive letters, alternate data streams) or a control character.
    /// </summary>
    public static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment == "." || segment == "..")
        {
            return false;
        }

        foreach (char c in segment)
        {
            if (c == '\\' || c == ':' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
