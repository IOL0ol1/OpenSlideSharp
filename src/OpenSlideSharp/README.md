# OpenSlideSharp

.NET bindings for [OpenSlide](https://openslide.org/) 4.x, a library for reading whole-slide images, with Deep Zoom and OME-Zarr generators.

```csharp
using OpenSlideSharp;

using var slide = OpenSlide.Open("slide.svs");
Console.WriteLine(slide.Properties[OpenSlideProperties.Vendor]);
Console.WriteLine($"{slide.Dimensions.Width}x{slide.Dimensions.Height}");
```

The native library is not included. Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package (win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64), or provide your own OpenSlide 4.x build.

## Known issues

On Windows, corrupt JPEG or PNG data in a slide terminates the whole process with `0xC0000409`, and the failure cannot be caught. The cause is in the Windows build of the native OpenSlide library, and no workaround is shipped. Services that open untrusted slides on Windows should decode them in a separate worker process. See [the details](https://github.com/IOL0ol1/OpenSlideSharp/blob/master/docs/known-issues/windows-cfg-longjmp-crash.md).
