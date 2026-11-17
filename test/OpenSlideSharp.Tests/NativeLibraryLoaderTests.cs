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
    public void SetLibraryPath_AfterLoad_Throws()
    {
        Assert.StartsWith("4.", OpenSlideLibrary.Version);
        Assert.Throws<InvalidOperationException>(() => OpenSlideLibrary.SetLibraryPath("whatever"));
    }
}
