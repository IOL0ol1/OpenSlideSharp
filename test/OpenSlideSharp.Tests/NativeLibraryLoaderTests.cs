using System;
using OpenSlideSharp.Native;
using Xunit;

namespace OpenSlideSharp.Tests;

public class NativeLibraryLoaderTests
{
    [Fact]
    public void FallbackNames_PerOs()
    {
        Assert.Empty(NativeLibraryLoader.GetFallbackNames(NativeOS.Windows));
        Assert.Equal(new[] { "libopenslide.so.1" }, NativeLibraryLoader.GetFallbackNames(NativeOS.Linux));
        Assert.Equal(new[] { "libopenslide.1.dylib", "/opt/homebrew/lib/libopenslide.1.dylib", "/usr/local/lib/libopenslide.1.dylib" },
                     NativeLibraryLoader.GetFallbackNames(NativeOS.MacOS));
    }

    [Fact]
    public void MissingLibraryMessage_ExplainsFix()
    {
        Assert.Contains("OpenSlideSharp.runtime", NativeLibraryLoader.MissingLibraryMessage);
        Assert.Contains("OPENSLIDE_LIBRARY_PATH", NativeLibraryLoader.MissingLibraryMessage);
    }

    [Fact] // Review Focus 4: DetectFormat, Open and Version surface this instead of a bare DllNotFoundException
    public void ThrowHelpfulIfMissing_WrapsDllNotFound()
    {
        var inner = new DllNotFoundException("raw loader message");
        var ex = Assert.Throws<DllNotFoundException>(() => NativeLibraryLoader.ThrowHelpfulIfMissing<int>(() => throw inner));
        Assert.Equal(NativeLibraryLoader.MissingLibraryMessage, ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(7, NativeLibraryLoader.ThrowHelpfulIfMissing(() => 7));
    }

    [Fact]
    public void ConfiguredPathException_NamesPathSourceAndOsReason()
    {
        var loadError = new DllNotFoundException("os reason: The specified module could not be found.");
        var ex = NativeLibraryLoader.CreateConfiguredPathException(
            @"C:\custom\libopenslide-1.dll", "the OPENSLIDE_LIBRARY_PATH environment variable", loadError);
        Assert.Contains(@"'C:\custom\libopenslide-1.dll'", ex.Message);
        Assert.Contains("OPENSLIDE_LIBRARY_PATH", ex.Message);
        Assert.Contains("os reason: The specified module could not be found.", ex.Message);
        Assert.DoesNotContain("OpenSlideSharp.runtime", ex.Message); // not the generic "nothing found" advice
        Assert.Same(loadError, ex.InnerException);
    }

    [Fact] // An explicitly configured path that fails must not be masked by the generic message.
    public void ThrowHelpfulIfMissing_PassesConfiguredPathFailureThrough()
    {
        var configured = NativeLibraryLoader.CreateConfiguredPathException(
            "/x/libopenslide.so", "OpenSlideLibrary.SetLibraryPath", new BadImageFormatException("wrong architecture"));
        var ex = Assert.Throws<DllNotFoundException>(() => NativeLibraryLoader.ThrowHelpfulIfMissing<int>(() => throw configured));
        Assert.Same(configured, ex);
    }

    [Fact]
    public void SetLibraryPath_AfterLoad_Throws()
    {
        Assert.StartsWith("4.", OpenSlideLibrary.Version);
        Assert.Throws<InvalidOperationException>(() => OpenSlideLibrary.SetLibraryPath("whatever"));
    }
}
