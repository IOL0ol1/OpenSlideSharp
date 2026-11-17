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
| `Default` | converted to sRGB; if the profile cannot be converted, behaves like `Embed` (a profile too large for a JPEG is then dropped) | none (embedded when the profile cannot be converted) |
| `Embed` | unchanged | the image's profile |
| `Ignore` | unchanged | none |

An image without an ICC profile is encoded the same way in all three modes. `Embed` throws `ArgumentException` for a JPEG when the profile is larger than the 255 × 65519 bytes a JPEG can hold.

## Conversion tiers

`SkiaColorTransform.Create(icc)` returns an `IColorTransform` that you can pass to `DeepZoomGenerator` and `OmeZarrOptions`, or `null` when the profile is not converted (the pixels are then left as they are). Only RGB profiles are considered: the data must hold a full 128-byte header with the data color space `RGB `. Gray, CMYK and other profiles are not converted, since Skia would apply them wrongly or not at all.

1. `SKColorSpace.CreateIcc`, for matrix/TRC profiles. When the result is sRGB itself, the transform leaves the pixels unchanged, and `ColorMode.Default` just drops the profile.
2. Lookup-table profiles that Skia parses but cannot turn into a color space: the pixels are encoded as an uncompressed PNG with the profile in an `iCCP` chunk and decoded again by `SKCodec` into sRGB. This tier is an allow-list of what was measured to match LittleCMS. It accepts only an A2B0 tag of type `lutAtoBType` (`mAB `) in a profile with a Lab PCS, which came within 1 level. skcms, which does the decoding, misreads the other cases:
   - With an XYZ PCS, it takes 0xFFFF rather than 0x8000 as 1.0, in `lut16` (`mft2`) and `lutAtoBType` alike. The result has half the linear light: white 255 → 188, mid gray 128 → 92.
   - With `lut16` and a Lab PCS, it reads the ICC v4 Lab encoding rather than the v2 one that `lut16` keeps, up to 7 levels off.
3. Anything else, including `lut8` (`mft1`), a missing A2B0 tag and a malformed tag table: no conversion, and `Create` returns `null`.

Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package for the native OpenSlide library.
