using System;
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
    /// Makes <paramref name="path"/> a new, empty output directory holding only the marker file, written first.
    /// </summary>
    /// <remarks>
    /// An existing directory is replaced only if it holds the marker (an earlier output, complete or not) or is empty.
    /// Nothing is deleted if a file has that name (such as the slide), or if the directory has no marker, is a root
    /// directory, holds the current directory or the slide, or holds a MIRAX slide's data directory.
    /// </remarks>
    /// <param name="path">The output directory.</param>
    /// <param name="slide">The slide being exported.</param>
    /// <exception cref="IOException">The directory may not be replaced.</exception>
    public static void PrepareDirectory(string path, string slide)
    {
        EnsureReplaceableDirectory(path, slide);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (File.Exists(Path.Combine(full, MarkerFileName)))
        {
            Directory.Delete(full, recursive: true);
        }

        Directory.CreateDirectory(full);
        File.WriteAllBytes(Path.Combine(full, MarkerFileName), []);
    }

    /// <summary>True if two output paths name the same directory: ignoring case on Windows and macOS.</summary>
    public static bool IsSamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
