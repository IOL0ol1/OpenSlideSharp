using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using OpenSlideSharp.SkiaSharp;

namespace OpenSlideSharp.Tool.Export;

/// <summary>What the two commands share: exit codes, opening the slide, replacing output and the color rules.</summary>
internal static class ExportSupport
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int InvalidArguments = 2;
    public const int Canceled = 130;

    /// <summary>Opens the slide, or writes why it cannot be opened (naming the file) and returns false.</summary>
    public static bool TryOpen(string path, TextWriter stderr, [NotNullWhen(true)] out OpenSlide? slide)
    {
        try
        {
            slide = OpenSlide.Open(path);
            return true;
        }
        catch (Exception ex) when (ex is OpenSlideException or ArgumentException or IOException or DllNotFoundException)
        {
            stderr.WriteLine($"openslide-sharp: cannot open '{path}': {ex.Message}");
            slide = null;
            return false;
        }
    }

    /// <summary>True for the errors an export reports as exit code 1 with their message, rather than a stack trace.</summary>
    public static bool IsExportError(Exception ex)
        => ex is OpenSlideException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException;

    /// <summary>
    /// The empty file the tool writes first into every output directory it creates. A later export deletes an existing
    /// directory only if it holds this file (or is empty), so it never deletes a directory it did not write.
    /// </summary>
    public const string MarkerFileName = ".openslide-sharp-output";

    /// <summary>
    /// Checks that the output file <paramref name="path"/> may be overwritten: it is not a directory and not the slide.
    /// An existing file is otherwise one the tool wrote before (a <c>.dzi</c>), so it may be replaced.
    /// </summary>
    /// <exception cref="IOException">It may not.</exception>
    public static void EnsureReplaceableFile(string path, string slide)
    {
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            throw new IOException($"Refusing to replace '{full}': it is a directory.");
        }

        if (string.Equals(full, Path.GetFullPath(slide), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Refusing to replace '{full}': it is the slide.");
        }
    }

    /// <summary>
    /// Checks that the output directory <paramref name="path"/> may be created, or replaced if it exists, without
    /// deleting anything: see <see cref="PrepareDirectory"/>.
    /// </summary>
    /// <exception cref="IOException">It may not.</exception>
    public static void EnsureReplaceableDirectory(string path, string slide)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (File.Exists(full))
        {
            // For example the slide itself: -r names its directory after the slide without its extension.
            throw new IOException($"Refusing to write the output directory '{full}': a file has that name.");
        }

        if (!Directory.Exists(full))
        {
            return;
        }

        // Defense in depth: none of these should ever hold a marker, but they must never be deleted.
        string slideFull = Path.GetFullPath(slide);
        bool isRoot = string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase);
        if (isRoot || IsSameOrInside(Environment.CurrentDirectory, full) || IsSameOrInside(slideFull, full))
        {
            throw new IOException($"Refusing to replace '{full}': it is a root directory, or holds the current directory or the slide.");
        }

        // MIRAX keeps name.mrxs next to a name/ directory with the slide data.
        if (string.Equals(Path.GetExtension(slideFull), ".mrxs", StringComparison.OrdinalIgnoreCase))
        {
            string data = Path.Combine(Path.GetDirectoryName(slideFull) ?? string.Empty, Path.GetFileNameWithoutExtension(slideFull));
            if (IsSameOrInside(data, full))
            {
                throw new IOException($"Refusing to replace '{full}': it holds the data directory of the slide.");
            }
        }

        if (!File.Exists(Path.Combine(full, MarkerFileName)) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new IOException(
                $"Refusing to replace '{full}': it is not empty and has no {MarkerFileName} file, so openslide-sharp did not write it. " +
                "Remove it or choose another output.");
        }
    }

    /// <summary>
    /// Makes <paramref name="path"/> a new, empty output directory holding only the marker file, written first:
    /// <see cref="MoveAside"/>, <see cref="DeleteOldOutput"/>, then <see cref="CreateOutputDirectory"/>.
    /// </summary>
    /// <remarks>
    /// An existing directory is replaced only if it holds the marker (an earlier output, complete or not) or is empty.
    /// Nothing is deleted if a file has that name (such as the slide), or if the directory has no marker, is a root
    /// directory, holds the current directory or the slide, or holds a MIRAX slide's data directory.
    /// </remarks>
    /// <param name="path">The output directory.</param>
    /// <param name="slide">The slide being exported.</param>
    /// <param name="stderr">Where a warning goes if the old output cannot be deleted.</param>
    /// <exception cref="IOException">The directory may not be replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">The old output cannot be moved aside.</exception>
    public static void PrepareDirectory(string path, string slide, TextWriter stderr)
    {
        string? old = MoveAside(path, slide, stderr);
        DeleteOldOutput(old, stderr);
        CreateOutputDirectory(path);
    }

    /// <summary>
    /// Checks that the output directory <paramref name="path"/> may be replaced (<see cref="EnsureReplaceableDirectory"/>),
    /// deletes what earlier replacements of it left behind, and, if it holds the marker, renames it to a hidden sibling
    /// (<c>.&lt;name&gt;.openslide-sharp-old-&lt;id&gt;</c>) so that the name is free.
    /// </summary>
    /// <remarks>
    /// The rename is one step: if it fails (on Windows, while a file in the directory is open), nothing has changed.
    /// A recursive delete in place could instead stop half way, without the marker that lets the next run go on.
    /// </remarks>
    /// <returns>The directory's new path, to pass to <see cref="DeleteOldOutput"/>; or null if there was nothing to move.</returns>
    /// <exception cref="IOException">The directory may not be replaced, or cannot be moved.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory cannot be moved.</exception>
    public static string? MoveAside(string path, string slide, TextWriter stderr)
    {
        EnsureReplaceableDirectory(path, slide);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        DeleteLeftovers(full, slide, stderr);
        if (!File.Exists(Path.Combine(full, MarkerFileName)))
        {
            return null; // missing or empty
        }

        string old = Path.Combine(Path.GetDirectoryName(full)!, LeftoverPrefix(full) + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(full, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Cannot replace '{full}', so nothing was changed: {ex.Message} Close any program using files in it and try again.", ex);
        }

        return old;
    }

    /// <summary>
    /// Deletes an old output moved aside by <see cref="MoveAside"/>, its marker last. If that fails, a warning names
    /// what is left, which the next export to the same output deletes; the export goes on, since the name is free.
    /// </summary>
    public static void DeleteOldOutput(string? old, TextWriter stderr)
    {
        if (old is null)
        {
            return;
        }

        try
        {
            DeleteMarkedDirectory(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine(
                $"openslide-sharp: warning: cannot delete the previous output, moved to '{old}': {ex.Message} " +
                "The next export to the same output deletes it; or delete it yourself.");
        }
    }

    /// <summary>Creates the output directory <paramref name="path"/> and writes the marker into it.</summary>
    public static void CreateOutputDirectory(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Directory.CreateDirectory(full);
        File.WriteAllBytes(Path.Combine(full, MarkerFileName), []);
    }

    /// <summary>
    /// The number of worker threads to use: <paramref name="jobs"/>, but at most 4 times the processor count, with a
    /// note on stderr when it is lowered. More threads only add memory (each holds a tile or chunk) and contention.
    /// </summary>
    public static int LimitJobs(int jobs, TextWriter stderr)
    {
        int max = 4 * Environment.ProcessorCount;
        if (jobs <= max)
        {
            return jobs;
        }

        stderr.WriteLine($"openslide-sharp: note: --jobs {jobs} is more than 4 times the processor count; using {max}.");
        return max;
    }

    /// <summary>True if two output paths name the same directory: ignoring case on Windows and macOS.</summary>
    public static bool IsSamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string LeftoverPrefix(string full) => "." + Path.GetFileName(full) + ".openslide-sharp-old-";

    // Deletes the old outputs that MoveAside left next to the output when they could not be deleted: only real
    // directories with the marker, so never one the tool did not write.
    private static void DeleteLeftovers(string full, string slide, TextWriter stderr)
    {
        string? parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        string prefix = LeftoverPrefix(full);
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        List<DirectoryInfo> leftovers;
        try
        {
            leftovers = new DirectoryInfo(parent).EnumerateDirectories()
                .Where(d => d.Name.StartsWith(prefix, comparison))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        string slideFull = Path.GetFullPath(slide);
        foreach (DirectoryInfo leftover in leftovers)
        {
            string path = Path.TrimEndingDirectorySeparator(leftover.FullName);
            if (leftover.LinkTarget is null
                && File.Exists(Path.Combine(path, MarkerFileName))
                && !IsSameOrInside(Environment.CurrentDirectory, path)
                && !IsSameOrInside(slideFull, path))
            {
                DeleteOldOutput(path, stderr);
            }
        }
    }

    // Deletes a directory the tool wrote, keeping its marker to the end, so that a delete that stops half way leaves
    // a directory a later run still recognizes. Links inside are removed, never followed.
    private static void DeleteMarkedDirectory(string directory)
    {
        var root = new DirectoryInfo(directory);
        foreach (FileSystemInfo entry in root.EnumerateFileSystemInfos().ToList())
        {
            if (entry is DirectoryInfo subdirectory)
            {
                subdirectory.Delete(recursive: subdirectory.LinkTarget is null);
            }
            else if (!string.Equals(entry.Name, MarkerFileName, StringComparison.Ordinal))
            {
                entry.Delete();
            }
        }

        File.Delete(Path.Combine(directory, MarkerFileName));
        root.Delete();
    }

    /// <summary>
    /// The Deep Zoom color rule for one image (the slide or an associated image, each with its own profile): the
    /// generator's transform and the mode to encode tiles with. A warning is written when the profile cannot be converted.
    /// </summary>
    public static (IColorTransform? Transform, ColorMode EncodeMode) DeepZoomColor(
        ReadOnlyMemory<byte> profile, ColorMode mode, string image, TextWriter stderr)
    {
        if (profile.IsEmpty || mode == ColorMode.Ignore)
        {
            return (null, ColorMode.Ignore);
        }

        if (mode == ColorMode.Embed)
        {
            return (null, ColorMode.Embed);
        }

        IColorTransform? transform = SkiaColorTransform.Create(profile);
        if (transform is null)
        {
            stderr.WriteLine($"openslide-sharp: warning: cannot convert the ICC profile of {image} to sRGB; embedding it in the tiles instead.");

            // Default encoding embeds the profile too, and drops one a JPEG cannot hold instead of failing.
            return (null, ColorMode.Default);
        }

        return (transform, ColorMode.Ignore);
    }

    /// <summary>
    /// The OME-Zarr color rule for one image: the generator's transform, or null to write the pixels unconverted.
    /// OME-Zarr cannot hold a profile, so a warning is written when one is not applied, except in Ignore mode.
    /// </summary>
    public static IColorTransform? OmeZarrColor(ReadOnlyMemory<byte> profile, ColorMode mode, string image, TextWriter stderr)
    {
        if (profile.IsEmpty || mode == ColorMode.Ignore)
        {
            return null;
        }

        if (mode == ColorMode.Embed)
        {
            stderr.WriteLine($"openslide-sharp: warning: OME-Zarr cannot hold the ICC profile of {image}; writing the pixels unconverted.");
            return null;
        }

        IColorTransform? transform = SkiaColorTransform.Create(profile);
        if (transform is null)
        {
            stderr.WriteLine($"openslide-sharp: warning: cannot convert the ICC profile of {image} to sRGB; writing the pixels unconverted.");
        }

        return transform;
    }

    /// <summary>Describes an image for warnings: <c>'small.svs'</c> or <c>associated image 'label' of 'small.svs'</c>.</summary>
    public static string Describe(string slide, string? associated)
        => associated is null ? $"'{Path.GetFileName(slide)}'" : $"associated image '{associated}' of '{Path.GetFileName(slide)}'";

    private static bool IsSameOrInside(string path, string directory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        // Case-insensitive everywhere: refusing too much is the safe side.
        return string.Equals(full, directory, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
