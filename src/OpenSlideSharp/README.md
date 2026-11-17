# OpenSlideSharp

.NET bindings for [OpenSlide](https://openslide.org/) 4.x, a library for reading whole-slide images, with Deep Zoom and OME-Zarr generators.

```csharp
using OpenSlideSharp;

using var slide = OpenSlide.Open("slide.svs");
Console.WriteLine(slide.Properties[OpenSlideProperties.Vendor]);
Console.WriteLine($"{slide.Dimensions.Width}x{slide.Dimensions.Height}");
```

The native library is not included. Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package (win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64), or provide your own OpenSlide 4.x build.
