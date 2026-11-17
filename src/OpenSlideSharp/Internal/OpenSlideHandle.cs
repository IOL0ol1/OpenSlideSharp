using System;
using System.Runtime.InteropServices;
using OpenSlideSharp.Native;

namespace OpenSlideSharp;

/// <summary>
/// Owns an <c>openslide_t*</c>. <see cref="SafeHandle"/> reference counting defers <c>openslide_close</c>
/// until every <see cref="HandleLease"/> taken on it has been released, so a <see cref="SafeHandle.Dispose()"/>
/// racing with in-flight native calls on other threads never frees the handle under them.
/// </summary>
internal sealed class OpenSlideHandle : SafeHandle
{
    internal OpenSlideHandle(IntPtr osr)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(osr);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// Takes a reference on the handle and checks the sticky error state before the caller's native call.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The handle has been closed.</exception>
    /// <exception cref="OpenSlideException">The slide is already in the error state.</exception>
    internal HandleLease Acquire()
    {
        bool added = false;
        DangerousAddRef(ref added);
        IntPtr osr = handle;
        try
        {
            ThrowIfError(osr);
        }
        catch
        {
            DangerousRelease();
            throw;
        }

        return new HandleLease(this, osr);
    }

    /// <summary>Throws <see cref="OpenSlideException"/> if <paramref name="osr"/> is in the error state.</summary>
    internal static unsafe void ThrowIfError(IntPtr osr)
    {
        string? error = Utf8.FromNative(openslide.get_error(osr));
        if (error != null)
        {
            throw new OpenSlideException(error);
        }
    }

    protected override bool ReleaseHandle()
    {
        openslide.close(handle);
        return true;
    }
}

/// <summary>
/// A counted reference to an open <c>openslide_t*</c>, obtained from <see cref="OpenSlideHandle.Acquire"/>
/// (normally through <c>OpenSlide.AcquireHandle</c>). <see cref="Handle"/> stays valid until <see cref="Dispose"/>,
/// even if the slide is disposed concurrently. Use it in a <c>using</c> statement and never copy it:
/// each copy would release the same reference.
/// </summary>
internal ref struct HandleLease
{
    private OpenSlideHandle? _owner;

    internal HandleLease(OpenSlideHandle owner, IntPtr osr)
    {
        _owner = owner;
        Handle = osr;
    }

    /// <summary>The raw <c>openslide_t*</c> to pass to the native call.</summary>
    public IntPtr Handle { get; }

    /// <summary>
    /// Checks the error state after the native call, then releases the reference. The reference is released
    /// even when that check throws <see cref="OpenSlideException"/>. Calling it again on the same variable does nothing.
    /// </summary>
    public void Dispose()
    {
        OpenSlideHandle? owner = _owner;
        if (owner is null)
        {
            return;
        }

        _owner = null;
        try
        {
            OpenSlideHandle.ThrowIfError(Handle);
        }
        finally
        {
            owner.DangerousRelease();
        }
    }
}
