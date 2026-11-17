using System;
using OpenSlideSharp.Native;
using Xunit;

namespace OpenSlideSharp.Tests;

// Semantics of OpenSlide.Invoke / AcquireHandle that the later property, region and associated-image code relies on.
public unsafe class HandleLeaseTests
{
    [Fact]
    public void AfterDispose_InvokeAndMethods_ThrowObjectDisposed()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        osr.Dispose();
        var ex = Assert.Throws<ObjectDisposedException>(() => osr.Invoke(static _ => 0));
        Assert.Equal(nameof(OpenSlide), ex.ObjectName);
        Assert.Throws<ObjectDisposedException>(() => osr.Invoke(static _ => { }));
        Assert.Throws<ObjectDisposedException>(() => osr.GetBestLevelForDownsample(1));
        Assert.Throws<ObjectDisposedException>(() => osr.Dimensions);
        Assert.Equal($"OpenSlide({TestFiles.Get("boxes.tiff")})", osr.ToString());
    }

    [Fact]
    public void StickyError_EveryMemberThrows_DisposeDoesNot()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Throws<OpenSlideException>(() => osr.Invoke(static h => StickyError.Trigger(h)));
        Assert.Throws<OpenSlideException>(() => osr.LevelCount);
        Assert.Throws<OpenSlideException>(() => osr.LevelDimensions);
        Assert.Throws<OpenSlideException>(() => osr.GetBestLevelForDownsample(1));
        Assert.Throws<OpenSlideException>(() => osr.Invoke(static _ => 0));
        osr.Dispose();
        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => osr.LevelCount);
    }

    [Fact]
    public void Lease_ReleasesReference_EvenWhenErrorCheckThrows()
    {
        byte[] path = Utf8.ToNullTerminated(TestFiles.Get("boxes.tiff"));
        IntPtr osr;
        fixed (byte* p = path)
        {
            osr = openslide.open(p);
        }

        var handle = new OpenSlideHandle(osr);
        Assert.Throws<OpenSlideException>(() =>
        {
            using HandleLease lease = handle.Acquire();
            StickyError.Trigger(lease.Handle); // the post-call check throws
        });
        bool acquired = false;
        Assert.Throws<OpenSlideException>(() =>
        {
            using HandleLease lease = handle.Acquire(); // pre-call check throws, so no lease is handed out
            acquired = true;
        });
        Assert.False(acquired);
        handle.Dispose();
        // IsClosed only becomes true when the reference count reaches zero, i.e. openslide_close actually ran.
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Lease_DisposeTwice_ReleasesOnce()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        HandleLease lease = osr.AcquireHandle();
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(4, osr.LevelCount); // a second release would have dropped the slide's own reference and closed it
    }

    [Fact]
    public void Dispose_WhileLeased_DefersCloseButRefusesNewCalls()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        HandleLease lease = osr.AcquireHandle();
        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => osr.LevelCount);
        Assert.Equal(4, openslide.get_level_count(lease.Handle)); // still open under the outstanding lease
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => osr.LevelCount);
    }
}
