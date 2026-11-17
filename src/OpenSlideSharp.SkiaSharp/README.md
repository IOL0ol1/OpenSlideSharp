# OpenSlideSharp.SkiaSharp

[SkiaSharp](https://github.com/mono/SkiaSharp) integration for [OpenSlideSharp](https://www.nuget.org/packages/OpenSlideSharp): JPEG and PNG encoding of `PixelImage`, and ICC to sRGB color conversion.

```csharp
using OpenSlideSharp;
using OpenSlideSharp.SkiaSharp;

using var slide = OpenSlide.Open("slide.svs");
byte[] png = slide.ReadRegion(0, 0, 0, 512, 512).Encode(ImageFormat.Png);
byte[] jpeg = slide.ReadRegion(0, 0, 0, 512, 512).Encode(ImageFormat.Jpeg, quality: 85, colorMode: ColorMode.Embed);
```

`ColorMode.Default` converts to sRGB and writes no profile (falling back to embedding the profile if it cannot be converted), `Embed` keeps the pixels and embeds the ICC profile, `Ignore` drops it. `SkiaColorTransform.Create` gives an `IColorTransform` for the Deep Zoom generator.

Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package for the native OpenSlide library.
