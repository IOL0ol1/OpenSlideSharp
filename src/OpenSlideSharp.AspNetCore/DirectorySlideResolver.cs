using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using OpenSlideSharp.AspNetCore.Internal;

namespace OpenSlideSharp.AspNetCore;

/// <summary>
/// Serves the slide files under a directory, as openslide-python's <c>deepzoom_multiserver.py</c> does: the
/// <c>{slide}</c> part of a URL is the file's path relative to the root, with <c>'/'</c> separators.
/// </summary>
/// <remarks>
/// <para>Only existing files inside the root resolve. These are rejected:</para>
/// <list type="bullet">
/// <item>Empty paths and empty segments (so leading, trailing and doubled <c>'/'</c>, and absolute paths).</item>
/// <item>The segments <c>.</c> and <c>..</c>, backslashes, colons (drive letters) and control characters.</item>
/// <item>On Windows, segments ending in a dot or a space, and device names such as <c>CON</c> or <c>NUL.svs</c>.</item>
/// <item>
/// Symbolic links and junctions that lead outside the root, whether directly, through further links or through
/// <c>..</c> in a link target. Links are followed the way the operating system follows them, starting at the
/// root. A link with an absolute target counts as inside only if the target starts with
/// <see cref="RootDirectory"/> as written, so a target that reaches the root through another path is rejected.
/// </item>
/// </list>
/// <para>A rejected path cannot be told apart from a missing file.</para>
/// </remarks>
public sealed class DirectorySlideResolver : ISlideResolver
{
    // The usual SYMLOOP_MAX; also stops link cycles.
    private const int MaxLinkHops = 40;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly char[] LinkSeparators = OperatingSystem.IsWindows() ? ['\\', '/'] : ['/'];

    private readonly string _rootPrefix;

    /// <summary>Creates a resolver for the files under a directory.</summary>
    /// <param name="rootDirectory">The directory to serve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is empty or not a valid path.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public DirectorySlideResolver(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The slide directory '{root}' does not exist.");
        }

        RootDirectory = root;
        _rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }

    /// <summary>Gets the full path of the root directory, without a trailing separator (unless it is a drive root).</summary>
    public string RootDirectory { get; }

    /// <inheritdoc/>
    public bool TryResolve(string urlPath, [NotNullWhen(true)] out string? filePath)
    {
        filePath = null;
        string[]? segments = urlPath is null ? null : UrlPath.SplitSafe(urlPath);
        if (segments is null || (OperatingSystem.IsWindows() && Array.Exists(segments, IsAmbiguousOnWindows)))
        {
            return false;
        }

        try
        {
            string candidate = _rootPrefix + string.Join(Path.DirectorySeparatorChar, segments);

            // If the OS would normalize anything away, the file opened would not be the one the URL names.
            if (!string.Equals(Path.GetFullPath(candidate), candidate, StringComparison.Ordinal)
                || !StaysInsideRoot(segments)
                || !File.Exists(candidate))
            {
                return false;
            }

            filePath = candidate;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Missing directories, files used as directories, unreadable links: all just "not found".
            return false;
        }
    }

    // Walks the path from the root one component at a time, replacing each symbolic link or junction by its target
    // and applying ".." to the real parent, as the OS does. Fails as soon as the walk would leave the root.
    private bool StaysInsideRoot(string[] segments)
    {
        var resolved = new List<string>(); // real components below the root; none of them is a link
        var pending = new Stack<string>(); // components still to walk, next on top
        PushAll(pending, segments);
        int hops = 0;

        while (pending.TryPop(out string? part))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (resolved.Count == 0)
                {
                    return false;
                }

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            string current = _rootPrefix + (resolved.Count == 0 ? part : string.Join(Path.DirectorySeparatorChar, resolved) + Path.DirectorySeparatorChar + part);
            string? target = new FileInfo(current).LinkTarget;
            if (target is null)
            {
                resolved.Add(part);
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                return false;
            }

            if (Path.IsPathRooted(target))
            {
                // An absolute target restarts the walk at the root, so it must name a path below the root as written.
                if (!Path.IsPathFullyQualified(target))
                {
                    return false;
                }

                string relative;
                if (target.StartsWith(_rootPrefix, PathComparison))
                {
                    relative = target.Substring(_rootPrefix.Length);
                }
                else if (string.Equals(Path.TrimEndingDirectorySeparator(target), RootDirectory, PathComparison))
                {
                    relative = string.Empty;
                }
                else
                {
                    return false;
                }

                resolved.Clear();
                PushAll(pending, relative.Split(LinkSeparators));
            }
            else
            {
                // A relative target is relative to the directory holding the link, which is where the walk is.
                PushAll(pending, target.Split(LinkSeparators));
            }
        }

        return true;
    }

    private static void PushAll(Stack<string> pending, string[] parts)
    {
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            pending.Push(parts[i]);
        }
    }

    // Win32 drops trailing dots and spaces from names, and maps device names, with any extension, to devices.
    private static bool IsAmbiguousOnWindows(string segment)
    {
        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return true;
        }

        ReadOnlySpan<char> stem = segment.AsSpan();
        int dot = stem.IndexOf('.');
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        stem = stem.TrimEnd(' ');
        return stem.Length switch
        {
            3 => stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase),
            4 => (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³'),
            6 => stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase),
            7 => stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
