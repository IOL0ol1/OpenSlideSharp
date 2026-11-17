# OpenSlideSharp.SkiaSharp

[SkiaSharp](https://github.com/mono/SkiaSharp) integration for [OpenSlideSharp](https://www.nuget.org/packages/OpenSlideSharp): JPEG and PNG encoding of `PixelImage`, and ICC to sRGB color conversion.

```csharp
using OpenSlideSharp;
using OpenSlideSharp.SkiaSharp;

using var slide = OpenSlide.Open("slide.svs");
byte[] png = slide.ReadRegion(0, 0, 0, 512, 512).Encode(ImageFormat.Png);
byte[] jpeg = slide.ReadRegion(0, 0, 0, 512, 512).Encode(ImageFormat.Jpeg, quality: 85, colorMode: ColorMode.Embed);
```

## Color modes

| `ColorMode` | Pixels | Output ICC |
|---|---|---|
| `Default` | converted to sRGB; if the profile cannot be converted, behaves like `Embed` | none |
| `Embed` | unchanged | the image's profile |
| `Ignore` | unchanged | none |

An image without an ICC profile is encoded the same way in all three modes.

## Conversion tiers

`SkiaColorTransform.Create(icc)` returns an `IColorTransform` that you can pass to `DeepZoomGenerator` and `OmeZarrOptions`. It tries three tiers and returns `null` when none works (the pixels are then left as they are):

1. `SKColorSpace.CreateIcc`, for matrix/TRC profiles.
2. For profiles that Skia reads as an ICC profile but cannot turn into a color space (typically lookup-table profiles), the pixels are encoded as a PNG with the profile in an `iCCP` chunk and decoded again by `SKCodec` into sRGB.
3. No conversion.

Tier 2 is built on skcms, which decodes the XYZ values of `lut16` profiles differently from LittleCMS. The colors are converted, but the result is darker than a LittleCMS-based reader would give (a mid gray of 128 comes out near 92 for the test profile).

Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package for the native OpenSlide library.
