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
| `Default` | converted to sRGB; if the profile cannot be converted, behaves like `Embed` (a profile too large for a JPEG is then dropped) | none |
| `Embed` | unchanged | the image's profile |
| `Ignore` | unchanged | none |

An image without an ICC profile is encoded the same way in all three modes. `Embed` throws `ArgumentException` for a JPEG when the profile is larger than the 255 × 65519 bytes a JPEG can hold.

## Conversion tiers

`SkiaColorTransform.Create(icc)` returns an `IColorTransform` that you can pass to `DeepZoomGenerator` and `OmeZarrOptions`, or `null` when the profile is not converted (the pixels are then left as they are). Only RGB profiles are considered: the data must hold a full 128-byte header with the data color space `RGB `. Gray, CMYK and other profiles are not converted, since Skia would apply them wrongly or not at all.

1. `SKColorSpace.CreateIcc`, for matrix/TRC profiles. When the result is sRGB itself, the transform leaves the pixels unchanged, and `ColorMode.Default` just drops the profile.
2. Profiles that Skia parses but cannot turn into a color space (lookup-table profiles): the pixels are encoded as an uncompressed PNG with the profile in an `iCCP` chunk and decoded again by `SKCodec` into sRGB. This tier refuses profiles whose A2B table (A2B0, or A2B1 if there is no A2B0) is a `lut16` (`mft2`). skcms, which does the decoding, misreads their PCS values: it takes 0xFFFF rather than 0x8000 as 1.0 for XYZ, and reads Lab in the ICC v4 encoding rather than the v2 one that `lut16` keeps. Compared with LittleCMS, an XYZ-PCS `lut16` profile comes out at half the linear light (white 255 → 188, mid gray 128 → 92), and a Lab-PCS one up to 7 levels off.
3. Anything else: no conversion, and `Create` returns `null`.

Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package for the native OpenSlide library.
