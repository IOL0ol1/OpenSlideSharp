using System;
using OpenSlideSharp.Native;

namespace OpenSlideSharp.Tests;

internal static unsafe class StickyError
{
    // A negative size makes OpenSlide set its sticky error deterministically. (Reading unreadable.svs would also do it,
    // but OpenSlide's libjpeg longjmp recovery currently fail-fasts .NET processes on Windows.)
    internal static int Trigger(IntPtr osr)
    {
        uint pixel;
        openslide.read_region(osr, &pixel, 0, 0, 0, -1, -1);
        return 0;
    }

    // Raw native call through a lease: the lease's post-call error check throws OpenSlideException.
    internal static void PutInErrorState(OpenSlide osr)
    {
        try
        {
            using HandleLease lease = osr.AcquireHandle();
            Trigger(lease.Handle);
        }
        catch (OpenSlideException)
        {
        }
    }
}
