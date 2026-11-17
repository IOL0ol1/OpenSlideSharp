using System;
using System.Runtime.InteropServices;
using System.Text;
using OpenSlideSharp.Native;
using Xunit;

namespace OpenSlideSharp.Tests;

public unsafe class NativeBindingTests
{
    [Fact]
    public void GetVersion_Is4x()
    {
        string? version = Marshal.PtrToStringUTF8((IntPtr)openslide.get_version());
        Assert.NotNull(version);
        Assert.StartsWith("4.", version);
    }

    [Fact]
    public void PropertyNameConstants_AreNulTerminated()
    {
        ReadOnlySpan<byte> vendor = openslide.OPENSLIDE_PROPERTY_NAME_VENDOR;
        Assert.Equal(0, vendor[^1]);
        Assert.Equal("openslide.vendor", Encoding.UTF8.GetString(vendor[..^1]));
    }
}
