using System;
using OpenSlideSharp.Native;

namespace OpenSlideSharp;

/// <summary>Information about, and configuration of, the native OpenSlide library.</summary>
public static class OpenSlideLibrary
{
    /// <summary>The library name used by the P/Invoke layer.</summary>
    public const string LibraryName = "libopenslide-1";

    /// <summary>
    /// Name of the environment variable holding an explicit path to the native library.
    /// It is honoured only on .NET 10 or later; the netstandard builds use the platform's default library search.
    /// </summary>
    public const string LibraryPathEnvironmentVariable = "OPENSLIDE_LIBRARY_PATH";

    /// <summary>Gets the version of the loaded native OpenSlide library (<c>openslide_get_version</c>).</summary>
    /// <exception cref="DllNotFoundException">The native library could not be loaded.</exception>
    public static unsafe string Version =>
        NativeLibraryLoader.ThrowHelpfulIfMissing(() => Utf8.FromNative(openslide.get_version()) ?? string.Empty);

#if NET
    /// <summary>
    /// Sets an explicit path to the native library. It takes precedence over
    /// <see cref="LibraryPathEnvironmentVariable"/> and must be called before the library is first used.
    /// If the library cannot be loaded from a configured path, OpenSlide calls throw
    /// <see cref="DllNotFoundException"/> naming that path and the operating system's reason; the default
    /// search is not tried.
    /// </summary>
    /// <param name="path">Full path of the native OpenSlide library.</param>
    /// <exception cref="System.InvalidOperationException">The library has already been resolved.</exception>
    public static void SetLibraryPath(string path) => NativeLibraryLoader.SetLibraryPath(path);
#endif
}
