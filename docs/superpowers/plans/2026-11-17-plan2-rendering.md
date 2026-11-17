# Plan 2：渲染（DeepZoom、OME-Zarr、SkiaSharp）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在核心包里交付与 openslide-python 一致的 `DeepZoomGenerator`、`OpenSlide.GetThumbnail` 和零依赖的 `OmeZarrGenerator`，并新增 `OpenSlideSharp.SkiaSharp` 包负责 JPEG/PNG 编码与 ICC→sRGB 转换。

**Architecture:** 所有像素都走同一条内部渲染流程：
1. 由 `SlideGeometry` 把目标层级上的矩形映射为 `read_region` 参数（逐行移植 `deepzoom.py`）；
2. 读出预乘 BGRA；
3. 按 Pillow 的公式合成到背景色；
4. 用 Pillow 同款定点 Lanczos-3 缩放到目标尺寸；
5. 可选地经 `IColorTransform` 转换颜色。

`DeepZoomGenerator` 与 `OmeZarrGenerator` 只负责各自的坐标划分和输出格式。缩放与合成和 Pillow 逐字节一致，所以测试可以直接和 openslide-python 生成的参考数据做精确比较。

**Tech Stack:** .NET 10 SDK、C#（latest）、xUnit v3、SkiaSharp 3.119.4、JsonSchema.Net 9.7.0（仅测试）、Python 3.12 + zarr 3.4.2 + ome-zarr 0.21.0（仅 CI 互操作检查）。

**Spec:** `docs/superpowers/specs/2026-11-17-openslidesharp-v4-refactor-design.md`（§6.1 其余部分、§6.3、§8）。路线图见 `docs/superpowers/plans/2026-11-17-openslidesharp-v4-roadmap.md`，计划 1 的遗留事项见 `docs/superpowers/notes/2026-11-17-plan1-carryover.md`。

## Global Constraints

- 核心包 `OpenSlideSharp`：
  - 目标框架 `netstandard2.0;netstandard2.1;net10.0`；
  - 零依赖，唯一的包依赖是 `System.Memory`，且仅限 `netstandard2.0`；
  - JSON 手写，不引入任何 JSON 库。
- `OpenSlideSharp.SkiaSharp`：
  - 目标框架同上；
  - 依赖 `OpenSlideSharp`（同版本）、`SkiaSharp` 3.119.4、`SkiaSharp.NativeAssets.Linux.NoDependencies` 3.119.4；
  - 不依赖 `OpenSlideSharp.runtime`。
- DeepZoom 与 Python 一致：
  - 层级、瓦片数、每个瓦片的 `read_region` 参数、瓦片尺寸、DZI 文本，与 `test/OpenSlideSharp.Tests/Oracles/deepzoom.json`（openslide-python 1.4.7 生成）完全一致。
  - 瓦片尺寸永远等于 `GetTileDimensions` 的值。这一点与 Pillow 的 `thumbnail` 可能差 1px 的行为不同，是设计决定。
  - 参考数据中 `exact: true` 的瓦片，像素必须逐字节相同。
- 构造参数默认值与 Python 相同：`tileSize = 254`、`overlap = 1`、`limitBounds = false`。
- 合成公式（Pillow `paste` + openslide-python `argb2rgba`）。设预乘像素为 `(c, a)`，背景色为 `bg`：
  - `a == 255`：结果为 `c`；
  - `a == 0`：结果为 `bg`；
  - 其他情况：先求 `u = min(255, 255 * c / a)`（整数截断），结果为 `DIV255(bg * (255 - a) + u * a)`，其中 `DIV255(v) = ((v + 128) >> 8) + (v + 128)) >> 8`。
  - 输出 alpha 恒为 255。
- 背景色：
  - 取属性 `openslide.background-color`（6 位十六进制 `RRGGBB`，不区分大小写）；
  - 缺失或格式不对时用白色。
- bounds 属性：`openslide.bounds-x/y/width/height` 按十进制整数解析，缺失或无法解析时视同缺失。
- mpp 属性：`openslide.mpp-x/y` 按 invariant culture 解析为 double，必须有限且大于 0，否则视同缺失。
- OME-Zarr 格式：
  - `NgffVersion.V0_5`（Zarr v3）为默认值，另支持 `V0_4`（Zarr v2）；
  - 轴为 `c, y, x`，数据类型 `uint8`，3 个通道（R、G、B，背景色已合成）；
  - 默认 chunk 为 `1×512×512`，边缘 chunk 用 0 补齐；
  - 压缩只有 `None` 和 `Gzip`，用 BCL 的 `System.IO.Compression` 实现。
- 颜色模式 `ColorMode`：
  - `Default`：转为 sRGB，输出不带 ICC。无法转换时退化为 `Embed`。
  - `Embed`：原样输出，并嵌入 ICC。
  - `Ignore`：丢弃 ICC。
  - 图像不带 ICC 时，三种模式的行为相同。
- ICC 在 API 上统一用 `ReadOnlyMemory<byte>` 表示，长度为 0 即“无”。
- `src/**` 项目开启 `TreatWarningsAsErrors` 并生成 XML 文档：所有公开成员都要写文档注释。
- 提交身份 `IOL0ol1 <1353839731@qq.com>`（仓库的 local config 已设置），提交信息末尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- Windows 上不读 `unreadable.svs` 的像素：沿用现有测试里的 `Assert.SkipWhen` 写法。

## Review Focus

1. **瓦片服务器的常态是多个线程同时对同一个生成器调用 `GetTile` / `GetChunk`**。要求结果与单线程完全一致，不能出现共享缓冲区被覆盖。→ Task 5 `GetTile_Parallel_MatchesSequential`、Task 6 `GetChunk_Parallel_MatchesSequential`。
2. **单层大图的低分辨率层级会要求读出巨大区域**（例如 100000×100000 的单层切片，其 DeepZoom 第 0 层要读整幅图）。超过 .NET 数组上限时，应抛出说明了区域尺寸的 `ArgumentOutOfRangeException`，不能溢出、崩溃或试图分配。→ Task 4 `Render_RegionTooLarge_ThrowsBeforeReading`。
3. **切片属性格式不对**（例如 `background-color = "zzz"`、`bounds-x = "abc"`、`mpp-x = "NaN"` 或负数）：生成器照常工作，回退到默认值，不抛异常。→ Task 4 `Geometry_MalformedProperties_FallBackToDefaults`、Task 6 `Metadata_InvalidMpp_UsesPixelUnits`。
4. **OME-Zarr key 来自 URL，是不可信输入**（`../`、`0/c/0/0/0/x`、`-1`、`01`、超出 int 的数字、空段、反斜杠）：`TryGetEntry` 只返回 `false`，不抛异常。→ Task 6 `TryGetEntry_HostileKeys_ReturnFalse`。
5. **扫描仪自带的 ICC 可能损坏或无法解析**：用 `ColorMode.Default` 编码时退化为 `Embed`，不能抛异常。SkiaSharp 的 `SKColorSpace.CreateIcc(byte[])` 遇到无法解析的数据会抛 `ArgumentNullException`，已实测。→ Task 2 `Encode_DefaultWithGarbageIcc_EmbedsProfile`。

## 已核实的事实（写计划时的实验结果，实施时直接采用）

- SkiaSharp 3.119.4 + 两个合成的测试 profile（`Fixtures/icc/`，由同目录的 `generate_icc.py` 生成，CC0 许可，内容为“红蓝互换的 sRGB”）：
  - `swap-rb-matrix.icc`（矩阵/TRC）：`SKColorSpaceIccProfile.Create` 成功，`SKColorSpace.CreateIcc` 成功。用 `SKPixmap.ReadPixels` 转到 sRGB 后，纯红 → `(0,0,255)`，灰 128 → 129。
  - `swap-rb-lut.icc`（只有 A2B0 lut16）：`SKColorSpaceIccProfile.Create` 成功，`SKColorSpace.CreateIcc` 返回 null。
    - 把像素编码为带 iCCP 的 **RGB**（color type 2）PNG，再用 `SKCodec.GetPixels` 以 `Bgra8888 / Opaque / sRGB` 解码，结果为纯红 → `(0,0,188)`、灰 128 → 92。红蓝互换已经生效，但亮度偏低：skcms 把 lut16 的 XYZ 当作 `/65535` 解码，LittleCMS 则得到精确的 `(0,0,255)`。
    - 若改用 RGBA PNG 并以 `Opaque` 解码，会返回 `InvalidConversion`。
  - 300 字节的随机数据：`SKColorSpaceIccProfile.Create` 返回 null。`SKColorSpace.CreateIcc(byte[])` 会抛 `ArgumentNullException`。SKCodec 遇到这样的 iCCP 时原样输出像素。
- `boxes.tiff`：
  - 尺寸 300×250，4 层，downsample 分别为 `1.0, 2.0, 4.016129032258064, 8.086312118570184`；
  - ICC 588 字节；mpp-x = mpp-y = `352.77777983377013`；没有 bounds 属性。
- `small.svs`：16×16，1 层，无 ICC，有关联图像 `thumbnail`（16×16）。
- `boxes_0.dcm`：16×16，1 层，ICC 524 字节，关联图像 `thumbnail` 带 456 字节 ICC。
- 参考数据位于 `test/OpenSlideSharp.Tests/Oracles/`，由 `generate_oracles.py` 用 openslide-python 1.4.7、openslide-bin 4.0.1.2、Pillow 13.0.0 生成，已提交：
  - `deepzoom.json`：
    - `geometry[]`：真实切片 × `(tileSize, overlap)` ∈ {(254,1),(256,0),(100,2),(64,5)} × `limitBounds`，记录全部瓦片；伪切片 × {(254,1),(512,0)} × `limitBounds`，记录抽样瓦片。每个 `tileInfo` 为 `[col, row, x, y, slideLevel, w, h, tileW, tileH]`。
    - `tiles[]`：瓦片 PNG，带 `expectedSize`、`pythonSize`、`exact`、`iccLength`。
  - `thumbnail.json`：`get_thumbnail` 的结果，带 `pythonSize`、`exact`。
  - `resample.json`：对 37×23 源图做纯 `LANCZOS` resize 的结果。
  - 所有 PNG 均不带 ICC。

---

### Task 1: 核心 API 调整（ICC、合成、颜色转换接口）

**Files:**
- Modify:
  - `src/OpenSlideSharp/PixelImage.cs`
  - `src/OpenSlideSharp/OpenSlide.cs`（`ColorProfile`、`ReadRegion` 两个重载的文档）
  - `src/OpenSlideSharp/AssociatedImage.cs`
  - 用到 `ColorProfile` / `IccProfile` 的现有测试
- Create:
  - `src/OpenSlideSharp/IColorTransform.cs`
  - `src/OpenSlideSharp/Internal/Rendering/Compositor.cs`
  - `test/OpenSlideSharp.Tests/CompositorTests.cs`

**Interfaces:**
- Produces（公开）：
  - `ReadOnlyMemory<byte> OpenSlide.ColorProfile { get; }`、`ReadOnlyMemory<byte> AssociatedImage.ColorProfile { get; }`：没有 profile 时返回空。仍然只读一次并缓存，之后每次返回同一个底层数组。
  - `PixelImage(int width, int height, PixelFormat format, byte[] data, ReadOnlyMemory<byte> iccProfile = default)`，以及 `ReadOnlyMemory<byte> PixelImage.IccProfile { get; }`。
  - `PixelImage PixelImage.ToOpaque(byte red, byte green, byte blue)`：
    - 返回新的 `Bgra32Opaque` 图像，ICC 原样保留；
    - 源图像为 `Bgra32Opaque` 时直接返回 `this`；
    - 源为 `Bgra32`（straight alpha）时不做反预乘，直接令 `u = c`，再套用同一个 `DIV255` 混合公式。
  - `string AssociatedImage.ToString()`：返回 `AssociatedImage(<name>)`。
  - `public interface IColorTransform { void Apply(Span<byte> pixels, int width, int height); }`：
    - 就地转换不透明 BGRA 像素，`stride = width * 4`；
    - 实现必须可以被多个线程同时调用；
    - 转换后的像素视为 sRGB。
- Produces（内部，供 Task 4 使用）：`static class Compositor`：
  - `static void CompositeInPlace(Span<byte> premultipliedBgra, byte red, byte green, byte blue)`：按 Global Constraints 的公式合成，就地输出不透明 BGRA。
  - `static bool TryParseBackground(string? hex, out byte red, out byte green, out byte blue)`：不合法时返回 `false`，三个输出都为 255。

- [ ] **Step 1: 写失败的测试 `CompositorTests`**

`CompositeInPlace_MatchesPillow`：输入为预乘 B,G,R,A 字节，背景以 R,G,B 给出，期望输出 B,G,R,A。

| 输入 B,G,R,A | 背景 R,G,B | 期望 B,G,R,A |
|---|---|---|
| 64,32,16,128 | 255,255,255 | 191,159,143,255 |
| 64,32,16,128 | 0,0,0 | 64,32,16,255 |
| 64,32,16,128 | 0x12,0x34,0x56 | 107,58,25,255 |
| 0,0,0,0 | 0x12,0x34,0x56 | 86,52,18,255 |
| 10,20,30,255 | 0x12,0x34,0x56 | 10,20,30,255 |
| 1,2,3,7 | 255,255,255 | 249,250,251,255 |
| 200,100,50,200 | 255,255,255 | 255,155,104,255 |
| 200,100,50,200 | 0x12,0x34,0x56 | 219,111,53,255 |

另外两个测试：
- `TryParseBackground_AcceptsHexCaseInsensitive`：`"ffFF00"` → `(255,255,0)`，返回 true。
- `TryParseBackground_RejectsMalformed`：`null`、`""`、`"zzzzzz"`、`"fff"`、`"#ffffff"`、`"fffffff"` 都返回 false，三个分量为 255。

在 `ReadRegionTests` 中补充：
- `ToOpaque_PremultipliedOverWhite`：用上表第 1 行的像素构造 1×1 `Bgra32Premultiplied`，期望与上表一致，`Format == Bgra32Opaque`，ICC 保留。
- `ToOpaque_OpaqueReturnsSameInstance`。

- [ ] **Step 2: 运行，确认失败（编译错误）**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 编译失败，缺少 `Compositor` 和 `ToOpaque`。

- [ ] **Step 3: 实现**

- ICC 改为 `ReadOnlyMemory<byte>`：内部仍缓存 `byte[]`，用 `new ReadOnlyMemory<byte>(array)` 暴露。
- `PixelImage` 构造函数补充校验：
  - `format` 必须是定义过的枚举值，否则 `ArgumentOutOfRangeException(nameof(format))`；
  - `width * 4` 必须不超过 `int.MaxValue`，否则 `ArgumentOutOfRangeException(nameof(width))`。
- 文档补充：
  - `ToStraightAlpha` 的 remarks 写明：`c > a` 的非法输入截断到 255，openslide-python 是按 uint8 回绕，属有意差异；
  - span 版 `ReadRegion` 写明：不保证目标缓冲区的对齐，OpenSlide 按 `uint32` 访问，传入 `byte[]`/`stackalloc`/池化数组均可。

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 全部通过（Windows 上原有 2 个跳过）。

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: expose ICC as ReadOnlyMemory, add opaque compositing and IColorTransform"
```

---

### Task 2: `OpenSlideSharp.SkiaSharp` 包（编码与颜色转换）

**Files:**
- Create:
  - `src/OpenSlideSharp.SkiaSharp/OpenSlideSharp.SkiaSharp.csproj`
  - `ColorMode.cs`、`ImageFormat.cs`、`PixelImageExtensions.cs`、`SkiaColorTransform.cs`
  - `Internal/IccEmbedding.cs`、`Internal/PngChunks.cs`（CRC32、zlib 封装、chunk 读写）
  - `README.md`
  - `test/OpenSlideSharp.Tests/Skia/EncodeTests.cs`、`test/OpenSlideSharp.Tests/Skia/ColorTransformTests.cs`
- Modify:
  - `Directory.Packages.props`（`SkiaSharp`、`SkiaSharp.NativeAssets.Linux.NoDependencies` 3.119.4）
  - `OpenSlideSharp.slnx`
  - `test/OpenSlideSharp.Tests/OpenSlideSharp.Tests.csproj`（ProjectReference 到 Skia 项目）
  - `eng/Test-Packages.ps1`、`test/PackageSmoke/*`

**Interfaces:**
- Consumes: Task 1 的 `IColorTransform`、`PixelImage.IccProfile`、`PixelImage.ToOpaque`。
- Produces（命名空间 `OpenSlideSharp.SkiaSharp`）：
  - `enum ColorMode { Default, Embed, Ignore }`
  - `enum ImageFormat { Jpeg, Png }`
  - `static class PixelImageExtensions`：
    - `SKBitmap ToSKBitmap(this PixelImage image)`：复制像素；`Bgra8888`；alpha 类型按 `Format` 分别为 Premul / Unpremul / Opaque；不设置色彩空间。
    - `SKImage ToSKImage(this PixelImage image)`。
    - `byte[] Encode(this PixelImage image, ImageFormat format, int quality = 75, ColorMode colorMode = ColorMode.Default)`：
      - `quality` 取值 0–100，否则 `ArgumentOutOfRangeException`；PNG 忽略该参数。
      - JPEG：非 `Bgra32Opaque` 的图像先 `ToOpaque(255,255,255)`。
      - `Default` 且有 ICC：取（缓存的）转换器。不为 null 时，在副本上转换后再编码，输出不带 ICC；为 null 时按 `Embed` 处理。若图像不是不透明的，先取出 alpha，按不透明像素转换颜色通道，再把 alpha 放回。
      - `Embed` 且有 ICC：编码后嵌入 ICC。
      - `Ignore`：直接编码。
  - `static class SkiaColorTransform`：`static IColorTransform? Create(ReadOnlyMemory<byte> iccProfile)`。
    - 空 profile、或 `SKColorSpaceIccProfile.Create` 失败（第 3 级），返回 null。
    - `SKColorSpace.CreateIcc(profile)` 成功时，返回第 1 级转换器（`SKPixmap.ReadPixels` 到 sRGB）。
    - 否则返回第 2 级转换器：编码为不压缩的 RGB PNG 并写入 iCCP，再用 `SKCodec` 以 sRGB 解码。
    - 不得对原始字节直接调用 `SKColorSpace.CreateIcc(byte[])`。
- 转换器缓存：
  - 以 ICC 的底层数组为键（`MemoryMarshal.TryGetArray`，且该段覆盖整个数组时才缓存），用 `ConditionalWeakTable<byte[], …>` 保存；
  - 切片的 ICC 数组实例在所有瓦片之间共享（Task 1 的语义），所以每个切片只判定一次。
- ICC 嵌入：
  - JPEG：在 SOI 之后、跳过开头连续的 APP0 段，插入 `APP2` 段，内容为 `"ICC_PROFILE\0"` + 序号（从 1 开始）+ 总段数。每段数据不超过 65519 字节。
  - PNG：在 IHDR 之后插入 `iCCP` chunk，profile 名为 `ICC Profile`，压缩方法 0，内容为 zlib 压缩的 profile。同时删除已有的 `iCCP`、`sRGB`、`gAMA`、`cHRM` chunk。
  - CRC32 与 zlib（`0x78 0x9C` 头 + `DeflateStream` + 大端 Adler-32）自行实现，以兼容 `netstandard2.0`。
- 包的 csproj：
  - `PackageId=OpenSlideSharp.SkiaSharp`；
  - Description 为 `SkiaSharp encoding (JPEG/PNG) and ICC to sRGB color conversion for OpenSlideSharp.`；
  - Tags 为 `openslide wsi pathology skiasharp icc`。
- 命名空间 `OpenSlideSharp.SkiaSharp` 内部引用 Skia 类型时，如果遇到歧义，用 `global::SkiaSharp`。

- [ ] **Step 1: 写失败的测试**

`ColorTransformTests` 用 `Fixtures/icc/*.icc`，输入像素依次为红、绿、蓝、灰 128：
- `Create_MatrixProfile_SwapsRedAndBlue`：转换后依次为 `(0,0,255)`、`(0,255,0)`、`(255,0,0)`、`(129,129,129)`（RGB 表示）。
- `Create_LutProfile_UsesCodecPathAndSwapsChannels`：红 → B ≥ 150 且 R ≤ 16、G ≤ 16；蓝 → R ≥ 150 且 G ≤ 16、B ≤ 16；绿 → G ≥ 150。只验证互换，不验证亮度，原因见“已核实的事实”。
- `Create_GarbageOrEmpty_ReturnsNull`：300 字节随机数据（`new Random(1)`）、`ReadOnlyMemory<byte>.Empty`。
- `Apply_ConcurrentCalls_AreConsistent`：8 个线程各转换 200 次 64×64 的随机图像，结果与单线程一致。

`EncodeTests`：
- `Encode_Png_RoundTripsPixels`：2×2 不透明图像，`Ignore` 模式。用 SKCodec 解码后像素相同，且不含 `iCCP`。
- `Encode_Jpeg_HasJfifSoiAndDecodes`：前两个字节为 `FF D8`，能被 SKCodec 解码，尺寸一致。
- `Encode_EmbedPng_ContainsExactProfile`：解析 PNG chunk，`iCCP` 解压后与 `boxes.tiff` 的 ICC 字节完全相同。
- `Encode_EmbedJpeg_ContainsExactProfile`：重组 APP2 `ICC_PROFILE` 段，结果与原 ICC 字节相同。另用一个 70000 字节的合成数据验证会被拆成 2 段。由于不是合法 ICC，只用于 `Embed`。
- `Encode_DefaultWithMatrixProfile_ConvertsAndDropsProfile`：1×1 纯红 + `swap-rb-matrix.icc`，编码 PNG 后解码为 `(0,0,255)`，且不含 `iCCP`。
- `Encode_DefaultWithGarbageIcc_EmbedsProfile`：PNG 里含 `iCCP`，内容就是这段垃圾数据，不抛异常。
- `Encode_Ignore_DropsProfile`。
- `Encode_JpegWithTransparentPixels_FlattensOnWhite`：1×1 `(0,0,0,0)` 预乘像素，编码 JPEG，解码后各通道 ≥ 250。
- `Encode_QualityOutOfRange_Throws`：取 -1 和 101。
- `ToSKBitmap_MapsAlphaType`：三种 `Format` 分别对应 `SKAlphaType.Premul`、`Unpremul`、`Opaque`。

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 编译失败（项目还不存在）。

- [ ] **Step 3: 实现包与测试项目引用，加入解决方案**

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 全部通过。

- [ ] **Step 5: 扩展包测试**

`eng/Test-Packages.ps1`：
- 打包时加入 `src/OpenSlideSharp.SkiaSharp`。
- 断言其 nuspec：
  - 每个 TFM 组都依赖 `OpenSlideSharp`（版本等于 `-Version`）、`SkiaSharp` 3.119.4、`SkiaSharp.NativeAssets.Linux.NoDependencies` 3.119.4；
  - 不依赖 `OpenSlideSharp.runtime`；
  - license 为 MIT。

`test/PackageSmoke`：
- 引用 `OpenSlideSharp.SkiaSharp`（版本同 `OpenSlideSharpVersion`）；
- 打开 `small.svs` 后，把 `ReadRegion(0,0,0,16,16).ToOpaque(255,255,255).Encode(ImageFormat.Png)` 的前 8 字节与 PNG 签名比较，不一致时退出码 4；
- 在已有输出里增加一行 `png=ok`，`Test-Packages.ps1` 在所有非 build-only 变体里断言这一行。

no-runtime 变体仍以退出码 3 结束（打开切片在编码之前就失败）。

Run: `pwsh eng/Get-OpenSlide.ps1 -Platform all; pwsh eng/Test-Packages.ps1`
Expected: 最后一行 `All package smoke tests passed.`

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: add OpenSlideSharp.SkiaSharp with JPEG/PNG encoding and ICC to sRGB conversion"
```

---

### Task 3: Pillow 同款 Lanczos 缩放

**Files:**
- Create:
  - `src/OpenSlideSharp/Internal/Rendering/LanczosResampler.cs`
  - `test/OpenSlideSharp.Tests/Rendering/ResamplerTests.cs`
  - `test/OpenSlideSharp.Tests/OracleData.cs`：读取 `Oracles/*.json`（`System.Text.Json`），并用 SkiaSharp 把 PNG 解码为 BGRA。解码时目标 `SKImageInfo` 的色彩空间设为 null，`AlphaType.Opaque`。
- Modify: `test/OpenSlideSharp.Tests/OpenSlideSharp.Tests.csproj`，加上 `<None Update="Oracles\**" CopyToOutputDirectory="PreserveNewest" />`。

**Interfaces:**
- Produces（内部）：`static class LanczosResampler`：
  - `static void Resize(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, Span<byte> destination, int width, int height)`。
  - 4 通道交错存放，每个通道独立处理，alpha 也参与计算（不透明输入的 alpha 保持 255）。
  - 尺寸相同时直接复制。
  - 任一尺寸 ≤ 0 时抛 `ArgumentOutOfRangeException`；缓冲区太短时抛 `ArgumentException`。
- Produces（测试辅助）：
  - `OracleData.Load<T>(string jsonFile)`
  - `OracleData.LoadPng(string file) → (int Width, int Height, byte[] Bgra)`

**算法**（移植 Pillow `libImaging/Resample.c`，必须逐字节一致）：

1. **系数**：对每个轴调用 `precompute_coeffs(inSize, in0 = 0, in1 = inSize, outSize)`。
   - `scale = filterscale = (double)inSize / outSize`；若 `filterscale < 1`，令 `filterscale = 1`。
   - `support = 3.0 * filterscale`；`ksize = (int)Math.Ceiling(support) * 2 + 1`。
   - 对每个输出位置 `xx`：
     - `center = (xx + 0.5) * scale`，`ss = 1.0 / filterscale`；
     - `xmin = (int)(center - support + 0.5)`，小于 0 时取 0；
     - `xmax = (int)(center + support + 0.5)`，大于 `inSize` 时取 `inSize`，然后 `xmax -= xmin`；
     - 对 `x` 从 0 到 `xmax - 1`：`w = lanczos((x + xmin - center + 0.5) * ss)`，并累加 `ww += w`；
     - 若 `ww != 0`，每个 `w /= ww`。
   - `lanczos(x)`：当 `-3 <= x < 3` 时为 `sinc(x) * sinc(x / 3)`，否则为 0。`sinc(0) = 1`，`sinc(x) = sin(πx) / (πx)`。
2. **定点化**：`PRECISION_BITS = 22`。
   - `k ≥ 0`：`(int)(0.5 + k * (1 << 22))`；
   - `k < 0`：`(int)(-0.5 + k * (1 << 22))`。
3. **累加**：每个通道 `int ss = 1 << 21; ss += src * k;`。结果用 `clip8` 截断：
   - `ss >= (1 << 30)` → 255；
   - `ss <= 0` → 0；
   - 其他 → `ss >> 22`。
4. **两遍处理**：
   - `needHorizontal = width != sourceWidth`，`needVertical = height != sourceHeight`。
   - 先做水平方向，只处理垂直系数用到的源行，即 `[boundsV[0], boundsV[last].min + boundsV[last].count)`。垂直方向的 bounds 相应减去起始行。中间结果为 8 位。
   - 再做垂直方向。
   - 两个方向都不需要时直接复制。

- [ ] **Step 1: 写失败的测试**

- `Resize_MatchesPillowExactly`（Theory）：数据来自 `resample.json` 的每个输出，`resample-src.png` 缩放后与对应 PNG 逐字节相同（比较 B、G、R，alpha 为 255）。尺寸覆盖：
  - 20×11、10×40（宽缩高放）、74×46（放大）、36×23（只缩水平）、37×5（只缩垂直）；
  - 4×3（比例约 9 倍，Pillow 的 resize 不做 reduce）、1×1、37×23（相同尺寸）。
- `Resize_ConstantImageStaysConstant`：53×31、每个通道均为 77 的图像，缩放到 7×29，结果全为 77。
- `Resize_InvalidArguments_Throw`：目标宽度为 0、源缓冲区太短。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add a Pillow-compatible Lanczos resampler"
```

---

### Task 4: 渲染源、几何映射、渲染器与 `GetThumbnail`

**Files:**
- Create（`src/OpenSlideSharp/Internal/Rendering/`）：`IRenderSource.cs`、`SlideRenderSource.cs`、`AssociatedImageRenderSource.cs`、`SlideGeometry.cs`、`RegionRenderer.cs`、`ThumbnailSize.cs`
- Create（测试）：`test/OpenSlideSharp.Tests/Rendering/FakeRenderSource.cs`、`GeometryTests.cs`、`RendererTests.cs`、`ThumbnailTests.cs`
- Modify: `src/OpenSlideSharp/OpenSlide.cs`（增加 `GetThumbnail`）

**Interfaces:**
- Consumes: Task 1 `Compositor`、Task 3 `LanczosResampler`、`IColorTransform`。
- Produces（公开）：`PixelImage OpenSlide.GetThumbnail(int maxWidth, int maxHeight)`。
  - 移植 openslide-python 的 `get_thumbnail`：
    1. `downsample = max(W / maxWidth, H / maxHeight)`（double）；
    2. `level = GetBestLevelForDownsample(downsample)`；
    3. 读出整层；
    4. 合成到背景色；
    5. 尺寸按 Pillow 的 `preserve_aspect_ratio` 计算：整层已经放得下时不缩放；需要缩放时用 `LanczosResampler`，不做 Pillow 的 reduce 预缩小；
    6. 结果为 `Bgra32Opaque`，ICC 为 `ColorProfile`。
  - `maxWidth` / `maxHeight` 小于 1 时抛 `ArgumentOutOfRangeException`。
  - 层级的宽或高为 0 时，返回合成后的空图像，不缩放。
- Produces（内部）：

```csharp
internal readonly record struct SourceRegion(long X, long Y, int Level, long Width, long Height);

internal interface IRenderSource
{
    IReadOnlyList<SlideSize> LevelDimensions { get; }
    IReadOnlyList<double> LevelDownsamples { get; }
    int GetBestLevelForDownsample(double downsample);
    string? GetProperty(string name);          // 不存在时为 null
    ReadOnlyMemory<byte> ColorProfile { get; }
    void ReadRegion(long x, long y, int level, int width, int height, Span<byte> destination); // 预乘 BGRA
    string Describe();                          // 用于 ToString，例如 "OpenSlide(path)"
}

internal sealed class SlideGeometry
{
    SlideGeometry(IRenderSource source, bool limitBounds);
    SlideSize ActiveSize { get; }                  // Python _l0_dimensions
    long OffsetX { get; } long OffsetY { get; }    // Python _l0_offset
    int LevelCount { get; }                        // 逐次减半直到 1×1 的层数（Python _dz_levels）
    SlideSize GetLevelSize(int scaleExponent);     // downsample 为 2^e 的层的尺寸，e=0 为全分辨率
    SourceRegion Map(int scaleExponent, long x, long y, long width, long height);
    (byte Red, byte Green, byte Blue) Background { get; }
    double? MppX { get; } double? MppY { get; }
}

internal sealed class RegionRenderer
{
    RegionRenderer(IRenderSource source, SlideGeometry geometry, IColorTransform? colorTransform);
    // 渲染 2^e 层上的矩形 (x, y, width, height)，输出不透明 BGRA
    void Render(int scaleExponent, long x, long y, int width, int height, Span<byte> destination);
}
```

- `SlideGeometry` 是 `deepzoom.py` 中 `__init__` 与 `_get_tile_info` 的通用版，要求逐行移植，并保持相同的 double 运算顺序。
  - `limitBounds`：
    - offset 取 bounds-x/y，缺失时为 0；
    - `scale = (double)(boundsSize ?? l0Size) / l0Size`；
    - 各层尺寸 `l = (long)Math.Ceiling(lLim * scale)`。
  - 各层尺寸从 `ActiveSize` 开始，按 `max(1, (z + 1) / 2)` 逐次减半。
  - 每层 `slideLevel = source.GetBestLevelForDownsample(2^e)`，`lz = (double)2^e / levelDownsamples[slideLevel]`。
  - `Map` 的计算：
    - `lx = lz * x`；
    - `X = (long)(levelDownsamples[slideLevel] * lx + OffsetX)`；
    - `Width = Math.Min((long)Math.Ceiling(lz * width), lDims[slideLevel].Width - (long)Math.Ceiling(lx))`；
    - y 方向相同。
- `RegionRenderer.Render` 的流程：
  1. `Map`；
  2. 区域宽或高 ≤ 0 时，整块填充背景色；
  3. 区域字节数超过 `int.MaxValue` 时，抛 `ArgumentOutOfRangeException`，消息里带上区域尺寸，而且不调用 `ReadRegion`；
  4. 用 `ArrayPool<byte>` 租缓冲区读取；
  5. `Compositor.CompositeInPlace`；
  6. 尺寸不同时调用 `LanczosResampler.Resize`；
  7. 如有颜色转换器，调用 `colorTransform.Apply`。
- `AssociatedImageRenderSource` 的约定：
  - 只有 1 层，downsample 为 1.0，`GetBestLevelForDownsample` 恒为 0；
  - 没有属性；ICC 为关联图像自己的 ICC；
  - 第一次读取时读出整幅图并缓存，线程安全；
  - `ReadRegion` 按 Python `ImageSlide.read_region` 的语义裁剪，区域超出图像的部分为透明 0。
- `ThumbnailSize.Compute(long width, long height, int maxWidth, int maxHeight) → (long W, long H)?`：逐行移植 Pillow 的 `preserve_aspect_ratio`，返回 null 表示不缩放。
  - `round_aspect(n, key) = max(min(floor(n), ceil(n), key = key), 1)`，两者相等时取 floor。
  - 当 `x / y >= aspect` 时：`x = round_aspect(y * aspect, n => |aspect - n / y|)`。
  - 否则：`y = round_aspect(x / aspect, n => n == 0 ? 0 : |aspect - x / n|)`。

- [ ] **Step 1: 写失败的测试**

- `GetThumbnail_MatchesPython`（Theory）：数据来自 `thumbnail.json` 的每一项。
  - 结果尺寸等于 `pythonSize`；
  - `exact` 为 true 时，像素与 PNG 逐字节相同；
  - ICC 长度为 588。
- `GetThumbnail_InvalidSize_Throws`：`(0, 10)`、`(10, -1)`。
- `Geometry_MatchesPython_ForFakes`：对 `deepzoom.json` 中所有 `kind == "fake"` 的项，用 `FakeRenderSource` 构造 `SlideGeometry`，检查：
  - `LevelCount == levelCount`；
  - DeepZoom 第 k 层对应 `GetLevelSize(levelCount - 1 - k) == levels[k].dimensions`；
  - 对 `tileInfo` 的每一项，按 Python `_get_tile_info` 的 overlap 规则算出 z 矩形后调用 `Map`，结果等于 `(x, y, slideLevel, w, h)`。
  - `FakeRenderSource` 由 `fake` 字段构造：层尺寸、downsample、属性；`GetBestLevelForDownsample` 用 OpenSlide 的算法，见 `generate_oracles.py` 的 `FakeSlide`。
- `Geometry_MalformedProperties_FallBackToDefaults`：属性为 `background-color = "zzz"`、`bounds-x = "abc"`、`bounds-width = "1e3"`、`mpp-x = "NaN"`、`mpp-y = "-1"`。
  - 期望白色背景、offset 为 (0, 0)、`ActiveSize` 等于第 0 层尺寸、`MppX` 和 `MppY` 为 null；
  - 构造时不抛异常。
- `Render_RegionTooLarge_ThrowsBeforeReading`：100000×100000 的单层伪切片，`Render(scaleExponent: 17, 0, 0, 1, 1, …)` 抛 `ArgumentOutOfRangeException`，且 `FakeRenderSource.ReadCount == 0`。
- `Render_EmptyRegion_FillsBackground`：
  - 构造单层 10×10 伪切片，属性为 `bounds-width = "0"`、`background-color = "112233"`，`limitBounds = true`；
  - 此时 `ActiveSize` 为 0×10；
  - `Render(0, 0, 0, 4, 4, …)` 输出的 16 个像素都应为 B=0x33、G=0x22、R=0x11、A=255，且 `ReadCount == 0`。
- `AssociatedImageSource_OutsideIsTransparent`：对 `small.svs` 的 thumbnail，`ReadRegion(-2, -2, 0, 4, 4)` 左上 2×2 为全 0，右下与图像 (0..1, 0..1) 的像素相同。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add the shared rendering pipeline and OpenSlide.GetThumbnail"
```

---

### Task 5: `DeepZoomGenerator`

**Files:**
- Create: `src/OpenSlideSharp/DeepZoomGenerator.cs`、`src/OpenSlideSharp/TileGridSize.cs`、`src/OpenSlideSharp/TileRegion.cs`、`test/OpenSlideSharp.Tests/DeepZoomTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `IRenderSource`、`SlideGeometry`、`RegionRenderer`。
- Produces（公开）：

```csharp
public readonly record struct TileGridSize(long Columns, long Rows);
public readonly record struct TileRegion(long X, long Y, int Level, long Width, long Height); // read_region 参数

public sealed class DeepZoomGenerator
{
    public DeepZoomGenerator(OpenSlide slide, int tileSize = 254, int overlap = 1, bool limitBounds = false, IColorTransform? colorTransform = null);
    public DeepZoomGenerator(AssociatedImage image, int tileSize = 254, int overlap = 1, IColorTransform? colorTransform = null);
    internal DeepZoomGenerator(IRenderSource source, int tileSize, int overlap, bool limitBounds, IColorTransform? colorTransform);
    public int TileSize { get; }
    public int Overlap { get; }
    public bool LimitBounds { get; }
    public int LevelCount { get; }
    public IReadOnlyList<TileGridSize> LevelTiles { get; }
    public IReadOnlyList<SlideSize> LevelDimensions { get; }
    public long TileCount { get; }
    public PixelImage GetTile(int level, long column, long row);
    public TileRegion GetTileCoordinates(int level, long column, long row);
    public SlideSize GetTileDimensions(int level, long column, long row);
    public string GetDzi(string format);
    public override string ToString();
}
```

- `GetTile`：
  - 结果为 `Bgra32Opaque`，尺寸等于 `GetTileDimensions`；
  - 未提供 `colorTransform` 时，ICC 为源的 ICC（与 Python 一致）；提供了时，ICC 为空。
- 参数校验：
  - 构造时 `tileSize < 1` 或 `overlap < 0` 抛 `ArgumentOutOfRangeException`；
  - `slide` / `image` 为 null 抛 `ArgumentNullException`；
  - 层级越界抛 `ArgumentOutOfRangeException(nameof(level))`，列、行越界抛 `ArgumentOutOfRangeException(nameof(column) / nameof(row))`。
- `GetDzi`：
  - 输出 `<Image TileSize="{t}" Overlap="{o}" Format="{format}" xmlns="http://schemas.microsoft.com/deepzoom/2008"><Size Width="{w}" Height="{h}" /></Image>`，没有 XML 声明，与 Python ElementTree 的输出逐字相同；
  - `format` 中的特殊字符按 ElementTree 转义：`&` → `&amp;`，`<` → `&lt;`，`>` → `&gt;`，`"` → `&quot;`，`\r` → `&#13;`，`\n` → `&#10;`，`\t` → `&#09;`；
  - `format` 为 null 时抛 `ArgumentNullException`。
- `ToString()`：返回 `DeepZoomGenerator(<source.Describe()>, tileSize: 254, overlap: 1, limitBounds: False)`。
- 所有属性在构造时一次性读出并缓存，包括 bounds、背景色、ICC，以及 slide 的层尺寸、downsample 和最佳层级。

- [ ] **Step 1: 写失败的测试**

- `Metadata_MatchesPython`（Theory）：数据来自 `deepzoom.json` 中所有 `kind == "slide"` 的项，用 `TestFiles.Get(source)` 打开。检查 `LevelCount`、`TileCount`、每层的 `LevelDimensions` 与 `LevelTiles`、`GetDzi("jpeg") == dziJpeg`、`GetDzi("png") == dziPng`；对每个 `tileInfo`，`GetTileCoordinates == (x, y, slideLevel, w, h)`，且 `GetTileDimensions == (tileW, tileH)`。
- `Metadata_MatchesPython_ForFakes`：同样的检查，对所有 `kind == "fake"` 的项用内部构造函数和 `FakeRenderSource` 执行。
- `GetTile_MatchesPython`（Theory）：数据来自 `deepzoom.json` 的 `tiles[]`，每一项用自身的 `tileSize`、`overlap` 构造生成器，`limitBounds` 为 false。
  - `source == "boxes.tiff"`：直接用该切片；`source == "boxes_0.dcm#thumbnail"`：用其关联图像 `thumbnail`。
  - 尺寸等于 `expectedSize`；`IccProfile.Length == iccLength`；
  - `exact` 为 true 时，B、G、R 与 PNG 逐字节相同。
- 来自 openslide-python `test_deepzoom.py`：
  - `GetTile_Boxes_Level9_1_0_Is47x250`；
  - `TileColorProfile_Is588Bytes`；
  - `GetTile_BadLevel_Throws`：层级 -1 和 10；
  - `GetTile_BadAddress_Throws`：`(0, -1, 0)`、`(0, 1, 0)`；
  - `GetTileCoordinates_Level9_1_0`：`(253, 0, 0, 47, 250)`；
  - `GetDzi_ContainsNamespace`。
- `ToString_DescribesParameters`：`DeepZoomGenerator(OpenSlide(<path>), tileSize: 254, overlap: 1, limitBounds: False)`。
- `GetDzi_EscapesFormat`：`GetDzi("a&b\"<>\n")` 中的属性为 `Format="a&amp;b&quot;&lt;&gt;&#10;"`。
- `GetTile_WithColorTransform_AppliesAndDropsProfile`：用一个把所有像素置为 `(1,2,3,255)` 的测试转换器，结果全部为该值，`IccProfile.IsEmpty`。
- `GetTile_Parallel_MatchesSequential`：`boxes.tiff`，参数 `(64, 5)`，最后 4 层的全部瓦片用 `Parallel.For` 生成，结果与顺序生成的逐字节相同。
- `GetTile_AfterSlideDisposed_ThrowsObjectDisposed`。
- `Constructor_InvalidParameters_Throw`：`tileSize: 0`、`overlap: -1`、`slide: null`。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add DeepZoomGenerator matching openslide-python"
```

---

### Task 6: `OmeZarrGenerator`

**Files:**
- Create:
  - `src/OpenSlideSharp/OmeZarrGenerator.cs`（含 `NgffVersion`、`OmeZarrCompression`、`OmeZarrOptions`，单文件）
  - `src/OpenSlideSharp/Internal/JsonText.cs`（极简 JSON 写入器）
  - `test/OpenSlideSharp.Tests/OmeZarrTests.cs`
  - `test/OpenSlideSharp.Tests/Schemas/ngff-0.4/image.schema`、`strict_image.schema`
  - `test/OpenSlideSharp.Tests/Schemas/ngff-0.5/image.schema`、`strict_image.schema`、`_version.schema`
- Modify:
  - `Directory.Packages.props`：`JsonSchema.Net` 9.7.0；
  - 测试 csproj：引用 JsonSchema.Net，并复制 `Schemas\**` 到输出目录。

Schema 文件从 `https://ngff.openmicroscopy.org/<0.4|0.5>/schemas/<name>` 原样下载。0.4 没有 `_version.schema`。在 `Schemas/README.md` 中注明来源 URL 与 BSD-2-Clause 许可。

**Interfaces:**
- Consumes: Task 4 的渲染组件；Task 5 用于交叉验证。
- Produces（公开）：

```csharp
public enum NgffVersion { V0_4, V0_5 }
public enum OmeZarrCompression { None, Gzip }

public sealed class OmeZarrOptions
{
    public NgffVersion Version { get; set; } = NgffVersion.V0_5;
    public int ChunkSize { get; set; } = 512;        // 16..8192
    public OmeZarrCompression Compression { get; set; } = OmeZarrCompression.None;
    public int GzipLevel { get; set; } = 6;          // 0..9
    public bool LimitBounds { get; set; }
    public IColorTransform? ColorTransform { get; set; }
    public int RenderCacheSize { get; set; } = 16;   // >= 0，0 表示不缓存
}

public sealed class OmeZarrGenerator
{
    public OmeZarrGenerator(OpenSlide slide, OmeZarrOptions? options = null);
    public OmeZarrGenerator(AssociatedImage image, OmeZarrOptions? options = null);   // 恒为单层
    internal OmeZarrGenerator(IRenderSource source, string name, bool singleLevel, OmeZarrOptions? options);
    public int LevelCount { get; }
    public IReadOnlyList<SlideSize> LevelDimensions { get; }   // 第 0 层为全分辨率
    public NgffVersion Version { get; }
    public int ChunkSize { get; }
    public byte[] GetChunk(int level, int channel, int chunkY, int chunkX);
    public bool TryGetEntry(string key, [NotNullWhen(true)] out byte[]? content);
    public IEnumerable<string> EnumerateKeys();
    public override string ToString();
}
```

- 构造时复制一份 options，之后外部修改 options 不影响生成器。不合法的值抛 `ArgumentOutOfRangeException`。
- 层级：
  - 第 0 层为 `SlideGeometry.GetLevelSize(0)`；
  - 第 ℓ 层为 `GetLevelSize(ℓ)`，一直生成到某层宽和高都不超过 `ChunkSize` 为止，该层是最后一层；
  - 关联图像只有 1 层。
- chunk 内容：
  - 第 ℓ 层 chunk `(cy, cx)` 的有效区域为 `x0 = cx * cs`，`w = min(cs, W - x0)`，y 方向相同，用 `RegionRenderer.Render(ℓ, x0, y0, w, h)` 渲染；
  - 通道 c（0 = R，1 = G，2 = B，分别取 BGRA 的字节偏移 2 / 1 / 0）组成 `cs × cs` 的平面，按行存放，超出有效区域的部分为 0；
  - `Gzip` 用 `GZipStream`：`net10.0` 上传入 `ZLibCompressionOptions { CompressionLevel = GzipLevel }`；netstandard 上把级别 0 映射为 `NoCompression`，1–5 映射为 `Fastest`，6–9 映射为 `Optimal`。
- 渲染缓存：
  - 键为 `(level, cy, cx)`，值为有效区域的不透明 BGRA，保存为 `Lazy<byte[]>`（ExecutionAndPublication），所以同一区域的三个通道只渲染一次；
  - 采用 LRU 淘汰，容量为 `RenderCacheSize`；
  - 渲染抛出异常时移除该项，以便之后重试。
- Key 与内容：

| 版本 | 元数据 key | chunk key |
|---|---|---|
| V0_5 | `zarr.json`（group）、`{l}/zarr.json`（array） | `{l}/c/{c}/{y}/{x}` |
| V0_4 | `.zgroup`、`.zattrs`、`{l}/.zarray` | `{l}/{c}/{y}/{x}` |

  - 数字段必须是不带前导零的十进制（`0` 本身合法），不允许空段、`.`、`..`、`\`、前导或尾随的 `/`，以及超出 int 范围的数字。
  - `.zmetadata`、`zarr.json` 下面的其他路径等，一律返回 `false`。
  - `EnumerateKeys` 的顺序：先列出全部元数据 key，再按 `l → c → y → x` 列出 chunk key。
- 元数据（紧凑 JSON，不含空白；数字用 `double.ToString("R", CultureInfo.InvariantCulture)` 格式化，整数用 `long.ToString(InvariantCulture)`）：
  - 公共的 `multiscales[0]`：
    - `name`：slide 为 `Path.GetFileName(slide.FileName)`，关联图像为其 `Name`；
    - `type`：`"lanczos"`；
    - `metadata`：`{"method":"OpenSlideSharp","description":"Rendered from OpenSlide levels with Lanczos-3 resampling"}`；
    - `axes`：`[{"name":"c","type":"channel"},{"name":"y","type":"space"[,"unit":"micrometer"]},{"name":"x","type":"space"[,"unit":"micrometer"]}]`，只有在 MppX 和 MppY 都有值时才带 `unit`；
    - `datasets[ℓ]`：`{"path":"ℓ","coordinateTransformations":[{"type":"scale","scale":[1, sy, sx]}(, {"type":"translation","translation":[0, ty, tx]})]}`。
      - 有 mpp 时 `sy = mppY * 2^ℓ`、`sx = mppX * 2^ℓ`；没有时 `sy = sx = 2^ℓ`。
      - `LimitBounds` 为 true 时才追加 translation：`ty = OffsetY * (mppY ?? 1)`，`tx = OffsetX * (mppX ?? 1)`。
  - 公共的 `omero`：

```json
{"channels":[{"label":"Red","color":"FF0000","window":{"start":0,"end":255,"min":0,"max":255},"active":true,"coefficient":1,"family":"linear","inverted":false},{"label":"Green","color":"00FF00",…},{"label":"Blue","color":"0000FF",…}],"rdefs":{"model":"color"}}
```

  - V0_5：
    - group `zarr.json` 为 `{"zarr_format":3,"node_type":"group","attributes":{"ome":{"version":"0.5","multiscales":[…],"omero":{…}}}}`；
    - array `zarr.json` 为：

```json
{"zarr_format":3,"node_type":"array","shape":[3,H,W],"data_type":"uint8","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[1,cs,cs]}},"chunk_key_encoding":{"name":"default","configuration":{"separator":"/"}},"fill_value":0,"codecs":[{"name":"bytes","configuration":{"endian":"little"}}(,{"name":"gzip","configuration":{"level":N}})],"dimension_names":["c","y","x"]}
```

  - V0_4：
    - `.zgroup` 为 `{"zarr_format":2}`；
    - `.zattrs` 为 `{"multiscales":[{"version":"0.4",…其余字段同上}],"omero":{…}}`；
    - `.zarray` 为 `{"chunks":[1,cs,cs],"compressor":null|{"id":"gzip","level":N},"dimension_separator":"/","dtype":"|u1","fill_value":0,"filters":null,"order":"C","shape":[3,H,W],"zarr_format":2}`。
- 文档注释写明：生成器不会自行记录日志。切片带 ICC 但没有提供 `ColorTransform` 时，像素按 `Ignore` 处理，由调用方（AspNetCore / 工具）负责记录警告。

- [ ] **Step 1: 写失败的测试**

除特别说明外，均使用 `boxes.tiff` 和 `ChunkSize = 64`：
- `Levels_HalveUntilOneChunk`：`LevelDimensions` 为 `300×250, 150×125, 75×63, 38×32`。`ChunkSize = 512` 时只有 1 层，为 300×250。
- `GroupMetadata_V05_ValidatesAgainstSchema` 与 `GroupMetadata_V04_ValidatesAgainstSchema`：分别对 `attributes`（0.5）和 `.zattrs`（0.4）校验 `image.schema` 与 `strict_image.schema`。JsonSchema.Net 需要先注册被 `$ref` 引用的 schema。
- `GroupMetadata_V05_Values`：
  - `axes` 的 y、x 带 `unit: "micrometer"`；
  - `datasets[0]` 的 scale 为 `[1, m, m]`，`datasets[3]` 的 scale 为 `[1, 8m, 8m]`，其中 `m = double.Parse("352.77777983377013", CultureInfo.InvariantCulture)`。按 double 精确比较。`"R"` 格式输出的文本分别为 `352.7777798337701` 和 `2822.222238670161`；
  - 没有 translation；`omero.channels` 依次为 Red / Green / Blue。
- `Metadata_LimitBounds_AddsTranslation`：伪切片 `pyramid4x-bounds` 带 mpp 0.25，`LimitBounds = true`。
  - `ActiveSize` 为 50001×40003；
  - `datasets[0]` 有 `translation: [0, 141.75, 308.5]`，即 567×0.25 和 1234×0.25。
- `Metadata_InvalidMpp_UsesPixelUnits`：`mpp-x = "NaN"` 时 y、x 都不带 unit，第 2 层的 scale 为 `[1, 4, 4]`。
- `ArrayMetadata_V05_Values` 与 `ArrayMetadata_V04_Values`：解析 JSON 后断言上面列出的每个字段；Gzip 时检查 codec 或 compressor。
- `GetChunk_EqualsDeepZoomTile`：对 4 个层级的每个 chunk，`DeepZoomGenerator(slide, 64, 0)` 第 `dz.LevelCount - 1 - ℓ` 层瓦片 `(cx, cy)` 的 R、G、B 值，等于 chunk 平面中有效区域的值，且有效区域以外全为 0。
- `GetChunk_Gzip_DecompressesToRaw`：Gzip 输出经 `GZipStream` 解压后，与 `None` 的输出相同。
- `TryGetEntry_ReturnsAllEnumeratedKeys`：`EnumerateKeys` 中的每一项都能取到。
  - 每个通道的 chunk 数：300×250 → 5×4 = 20，150×125 → 3×2 = 6，75×63 → 2×1 = 2，38×32 → 1×1 = 1，合计 29。
  - V0_5 共 `1 + 4 + 3 × 29 = 92` 个 key；V0_4 共 `2 + 4 + 87 = 93` 个 key。
- `TryGetEntry_HostileKeys_ReturnFalse`：`""`、`"/"`、`"../zarr.json"`、`"0/c/0/0/0/0"`、`"0/c/3/0/0"`、`"0/c/-1/0/0"`、`"0/c/01/0/0"`、`"0/c/0/0/99999999999"`、`"9/zarr.json"`、`".zmetadata"`、`"0\\zarr.json"`、`"zarr.json/"`、`"0//c/0/0/0"`，以及 V0_4 下的 `"zarr.json"`。全部返回 false，不抛异常。
- `GetChunk_OutOfRange_Throws`。
- `GetChunk_Parallel_MatchesSequential`：16 个线程打乱顺序取全部 chunk，结果与顺序获取一致。
- `AssociatedImage_IsSingleLevel`：`small.svs` 的 thumbnail，`LevelCount == 1`，`name` 为 `"thumbnail"`。
- `Options_AreCopiedAndValidated`：`ChunkSize` 取 15 和 8193、`GzipLevel` 取 10、`RenderCacheSize` 取 -1 时都抛异常；构造之后修改 options 不影响已有生成器。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add a zero-dependency OME-Zarr (NGFF 0.4/0.5) generator"
```

---

### Task 7: OME-Zarr 互操作检查、文档与遗留事项

**Files:**
- Create:
  - `test/OpenSlideSharp.Tests/OmeZarrInteropExport.cs`
  - `eng/verify-omezarr.py`
  - `eng/requirements-interop.txt`：`zarr==3.4.2`、`ome-zarr==0.21.0`、`numpy==2.5.5`
  - `docs/superpowers/notes/2026-11-17-plan2-carryover.md`
- Modify:
  - `.github/workflows/ci.yml`（新增 `omezarr-interop` job）
  - `README.md`
  - `src/OpenSlideSharp/README.md`
  - `src/OpenSlideSharp.SkiaSharp/README.md`
  - `docs/superpowers/specs/2026-11-17-openslidesharp-v4-refactor-design.md`（把 §6.1 和 §8 中的 `OpenSlide.LibraryVersion` 改为 `OpenSlideLibrary.Version`）

**Interfaces:**
- `OmeZarrInteropExport`：一个 xUnit 测试 `Export_ForPythonInterop`。
  - 读取环境变量 `OPENSLIDESHARP_INTEROP_DIR`，未设置时写到临时目录，测试结束后删除。
  - 用 `boxes.tiff`、`ChunkSize = 64`，导出四个 store：`v05-none.zarr`、`v05-gzip.zarr`、`v04-none.zarr`、`v04-gzip.zarr`。导出方式是遍历 `EnumerateKeys()`，以 `/` 分隔写成文件。
  - 每层另写 `expected/{l}.rgb`：平面 `c, y, x` 的 uint8 原始字节，通过 DeepZoom（`tileSize = 64`、`overlap = 0`）拼出整层后取 R、G、B。
  - 写 `manifest.json`：`{"stores":[…],"levels":[[3,H,W],…]}`。
  - 测试本身只断言文件都已写出。
- `eng/verify-omezarr.py <dir>`：
  - 用 `zarr.open_group(store, mode="r")` 打开每个 store，逐层比较 `np.asarray(group[str(l)])` 与 `expected/{l}.rgb`；
  - 再用 `ome_zarr.reader.Reader(ome_zarr.io.parse_url(store))` 读取，第一个节点的 `data` 层数和各层 shape 必须与 manifest 一致；
  - 任一不符时以非零退出码结束，并打印原因。
- CI 新增 `omezarr-interop` job：
  - 运行在 ubuntu-latest，`needs: test`，`timeout-minutes: 30`；
  - 步骤：checkout、setup-dotnet、cache、`./eng/Get-OpenSlide.ps1`；`actions/setup-python` 安装 3.12，执行 `pip install -r eng/requirements-interop.txt`；
  - 设置 `OPENSLIDESHARP_INTEROP_DIR=${{ runner.temp }}/omezarr`，运行 `dotnet test OpenSlideSharp.slnx -c Release`；
  - 最后运行 `python eng/verify-omezarr.py ${{ runner.temp }}/omezarr`。
  - `actions/setup-python` 用当前最新的主版本 tag，和其他 action 一样写主版本号。

- [ ] **Step 1: 实现导出测试与 Python 校验脚本，并在本机验证**

Run（Windows，任意 Python 3.12 环境）：

```
pip install -r eng/requirements-interop.txt
$env:OPENSLIDESHARP_INTEROP_DIR="$PWD/artifacts/omezarr-interop"
dotnet test OpenSlideSharp.slnx -c Release
python eng/verify-omezarr.py artifacts/omezarr-interop
```

Expected: 测试全部通过，脚本输出 `OK: 4 stores verified` 并以 0 退出。

- [ ] **Step 2: 加入 CI job**
- [ ] **Step 3: 更新文档**

- 根 README：
  - 包表格增加 `OpenSlideSharp.SkiaSharp`；
  - Usage 增加 DeepZoom 和 OME-Zarr 的最小示例，以及 `Encode(ImageFormat.Jpeg)`。
- 核心包 README：补充 DeepZoom、OME-Zarr 和 `GetThumbnail` 的示例。
- Skia 包 README：
  - 说明 `ColorMode` 三种模式，以及三级转换策略；
  - 说明第 2 级对 lut16-XYZ profile 的亮度偏差，这是 skcms 的行为。
- 规格文档：把 `OpenSlide.LibraryVersion` 改为 `OpenSlideLibrary.Version`。

- [ ] **Step 4: 写计划 2 的遗留事项文件**：汇总各任务审阅中被延后的事项，格式同计划 1。
- [ ] **Step 5: 运行全部测试，提交**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 全部通过。

```bash
git add -A && git commit -m "test: verify OME-Zarr output with zarr-python and ome-zarr; document rendering APIs"
```
