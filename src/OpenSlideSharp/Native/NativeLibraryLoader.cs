using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
#if NET
using System.Reflection;
using System.Runtime.CompilerServices;
#endif

namespace OpenSlideSharp.Native;

internal enum NativeOS
{
    Windows,
    Linux,
    MacOS,
}

internal static class NativeLibraryLoader
{
    internal static string MissingLibraryMessage =>
        "The native OpenSlide library (" + OpenSlideLibrary.LibraryName + ") could not be loaded. " +
        "Add the OpenSlideSharp.runtime package (it bundles the native binaries for Windows, Linux and macOS) " +
        "or install OpenSlide 4.x on this machine. On .NET 10 or later you can instead set the " +
        OpenSlideLibrary.LibraryPathEnvironmentVariable + " environment variable, or call " +
        "OpenSlideLibrary.SetLibraryPath, with the full path of the library.";

    /// <summary>
    /// Key in <see cref="Exception.Data"/> that marks a failure to load a library path the user configured; its value
    /// is that path. <see cref="ThrowHelpfulIfMissing{T}"/> passes such exceptions through unchanged.
    /// </summary>
    internal const string ConfiguredPathDataKey = "OpenSlideSharp.ConfiguredLibraryPath";

    /// <summary>
    /// Runs <paramref name="call"/>, which may be the process's first P/Invoke into OpenSlide, and replaces a bare
    /// <see cref="DllNotFoundException"/> with one whose message explains how to supply the native library. A failure
    /// to load an explicitly configured path (see <see cref="CreateConfiguredPathException"/>) already says what went
    /// wrong, so it propagates as is.
    /// </summary>
    internal static T ThrowHelpfulIfMissing<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (DllNotFoundException ex) when (!ex.Data.Contains(ConfiguredPathDataKey))
        {
            throw new DllNotFoundException(MissingLibraryMessage, ex);
        }
    }

    /// <summary>
    /// Builds the exception for a configured library path that failed to load: the message names the path, where
    /// it was configured and the operating system's reason, and <paramref name="loadError"/> becomes the
    /// <see cref="Exception.InnerException"/>.
    /// </summary>
    /// <param name="path">The configured path.</param>
    /// <param name="source">Where the path came from, for example <c>OpenSlideLibrary.SetLibraryPath</c>.</param>
    /// <param name="loadError">The exception the native library loader threw.</param>
    internal static DllNotFoundException CreateConfiguredPathException(string path, string source, Exception loadError)
    {
        var ex = new DllNotFoundException(
            "Unable to load the native OpenSlide library from '" + path + "', the path configured by " + source +
            ": " + loadError.Message,
            loadError);
        ex.Data[ConfiguredPathDataKey] = path;
        return ex;
    }

    internal static IReadOnlyList<string> GetFallbackNames(NativeOS os) => os switch
    {
        NativeOS.Linux => new[] { "libopenslide.so.1" },
        NativeOS.MacOS => new[]
        {
            "libopenslide.1.dylib",
            "/opt/homebrew/lib/libopenslide.1.dylib",
            "/usr/local/lib/libopenslide.1.dylib",
        },
        _ => Array.Empty<string>(),
    };

    internal static NativeOS CurrentOS =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? NativeOS.Windows :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? NativeOS.MacOS :
        NativeOS.Linux;

#if NET
    private static readonly object Gate = new();
    private static string? s_explicitPath;
    private static IntPtr s_handle;

    internal static bool IsResolved
    {
        get
        {
            lock (Gate)
            {
                return s_handle != IntPtr.Zero;
            }
        }
    }

    internal static void SetLibraryPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (Gate)
        {
            if (s_handle != IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The native OpenSlide library has already been resolved; call SetLibraryPath before first use.");
            }

            s_explicitPath = path;
        }
    }

#pragma warning disable CA2255 // The resolver must be registered before any P/Invoke runs; there is no other hook.
    [ModuleInitializer]
    internal static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(typeof(OpenSlideLibrary).Assembly, Resolve);
    }
#pragma warning restore CA2255

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != OpenSlideLibrary.LibraryName)
        {
            return IntPtr.Zero;
        }

        lock (Gate)
        {
            if (s_handle != IntPtr.Zero)
            {
                return s_handle;
            }

            string? explicitPath = s_explicitPath;
            string source = "OpenSlideLibrary.SetLibraryPath";
            if (string.IsNullOrEmpty(explicitPath))
            {
                explicitPath = Environment.GetEnvironmentVariable(OpenSlideLibrary.LibraryPathEnvironmentVariable);
                source = "the " + OpenSlideLibrary.LibraryPathEnvironmentVariable + " environment variable";
            }

            if (!string.IsNullOrEmpty(explicitPath))
            {
                // Load, not TryLoad: its exception carries the OS reason (file missing, wrong architecture,
                // a dependency missing), which the user needs to fix the path they configured.
                try
                {
                    return s_handle = NativeLibrary.Load(explicitPath);
                }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
                {
                    throw CreateConfiguredPathException(explicitPath, source, ex);
                }
            }

            if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out IntPtr handle))
            {
                return s_handle = handle;
            }

            foreach (string name in GetFallbackNames(CurrentOS))
            {
                if (NativeLibrary.TryLoad(name, out handle))
                {
                    return s_handle = handle;
                }
            }

            return IntPtr.Zero;
        }
    }
#endif
}
