using System;
using System.Runtime.InteropServices;
using OpenSlideSharp.Native;

namespace OpenSlideSharp;

/// <summary>Owns an <c>openslide_cache_t*</c> and releases the caller's reference with <c>openslide_cache_release</c>.</summary>
internal sealed class CacheHandle : SafeHandle
{
    internal CacheHandle(IntPtr cache)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(cache);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        openslide.cache_release(handle);
        return true;
    }
}
