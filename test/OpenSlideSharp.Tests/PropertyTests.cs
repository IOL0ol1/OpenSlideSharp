using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using OpenSlideSharp.Native;
using Xunit;

namespace OpenSlideSharp.Tests;

public class PropertyTests
{
    private delegate ReadOnlySpan<byte> SpanGetter();

    [Fact]
    public void Properties_Boxes()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal("generic-tiff", osr.Properties[OpenSlideProperties.Vendor]);
        Assert.Throws<KeyNotFoundException>(() => osr.Properties["__does_not_exist"]);
        Assert.False(osr.Properties.TryGetValue("__does_not_exist", out _));
        Assert.Equal(osr.Properties.Count, osr.Properties.Count());
        Assert.True(osr.Properties.ContainsKey(OpenSlideProperties.Vendor));
        Assert.False(osr.Properties.ContainsKey("__does_not_exist"));
        Assert.Contains(OpenSlideProperties.Vendor, osr.Properties.Keys);
        Assert.Equal(osr.Properties.Count, osr.Properties.Values.Count());
    }

    [Fact] // Native code would stop at the NUL and find "openslide.vendor"; documented on OpenSlide.Properties.
    public void Properties_KeyWithEmbeddedNul_Throws()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        const string key = OpenSlideProperties.Vendor + "\0x";
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => osr.Properties.ContainsKey(key)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => osr.Properties.TryGetValue(key, out _)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => osr.Properties[key]).ParamName);
    }

    [Fact] // Hand-written constants must exist in the generated layer with equal values (one-way: nightly may add more).
    public void PropertyConstants_MatchNativeHeader()
    {
        var native = typeof(openslide).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.Name.StartsWith("OPENSLIDE_PROPERTY_NAME_"))
            .Select(NativeConstant).ToHashSet();
        var fields = typeof(OpenSlideProperties).GetFields(BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(13, fields.Length);
        foreach (var f in fields)
            Assert.Contains((string)f.GetRawConstantValue()!, native);
    }

    [Fact]
    public void Properties_AfterDispose_Throws()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var props = osr.Properties;
        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => props[OpenSlideProperties.Vendor]);
        Assert.Throws<ObjectDisposedException>(() => { _ = props.Count; });
        Assert.Throws<ObjectDisposedException>(() => { props.ToList(); });
        Assert.Throws<ObjectDisposedException>(() => props.TryGetValue(OpenSlideProperties.Vendor, out _));
    }

    private static string NativeConstant(PropertyInfo p)
    {
        var getter = p.GetMethod!.CreateDelegate<SpanGetter>();
        ReadOnlySpan<byte> span = getter();
        if (span.Length > 0 && span[span.Length - 1] == 0) span = span.Slice(0, span.Length - 1);
        return Encoding.UTF8.GetString(span);
    }
}
