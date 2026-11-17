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
        "Add the OpenSlideSharp.runtime package (it bundles the native binaries for Windows, Linux and macOS), " +
        "install OpenSlide 4.x on this machine, or set the " + OpenSlideLibrary.LibraryPathEnvironmentVariable +
        " environment variable to the full path of the library.";

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
            if (string.IsNullOrEmpty(explicitPath))
            {
                explicitPath = Environment.GetEnvironmentVariable(OpenSlideLibrary.LibraryPathEnvironmentVariable);
            }

            if (!string.IsNullOrEmpty(explicitPath))
            {
                if (!NativeLibrary.TryLoad(explicitPath, out IntPtr explicitHandle))
                {
                    throw new DllNotFoundException(
                        "Unable to load the native OpenSlide library from the explicitly configured path '" +
                        explicitPath + "'.");
                }

                return s_handle = explicitHandle;
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
