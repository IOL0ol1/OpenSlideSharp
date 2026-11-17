# 计划 2 遗留事项（供计划 3–5 使用）

来源：计划 2（渲染）各任务审阅、整体终审及其修复轮的分级结果。下列事项都已评估为**不阻塞计划 2 合并**，按应在哪个计划处理分组。计划 1 遗留事项中属于计划 2 的部分（ICC 共享、`Properties` 实时查询、单层源抽象、`Unpremultiply` 与对齐的文档、规格中的 `LibraryVersion`）已在计划 2 中处理。

## 计划 3（AspNetCore、工具）必须处理

- **告警由调用方负责**：
  - 核心生成器不记录日志。下面两种情况由 AspNetCore 和工具按切片各记录一次警告：
    - 切片带 ICC，但没有提供 `IColorTransform`。此时 OME-Zarr 按 Ignore 输出。
    - `SkiaColorTransform.Create` 返回 null，即第 3 级。此时 DeepZoom 退化为 Embed，OME-Zarr 退化为 Ignore。
- **`ColorMode.Default` 还没有在结构上保证不抛异常**：第 1、2 级的 `Apply` 内部，Skia 的 `ReadPixels` 或 `SKCodec` 失败时会抛 `InvalidOperationException`。`Encode` 应在 Default 分支捕获它并退化为 Embed，Skia 包 README 的颜色模式表也要同步修改。
- **README 颜色模式表**：Default 一行的 “Output ICC” 写的是 none。退化为 Embed 时，输出实际带有 profile。
- **生成器要按切片缓存，不要每个请求新建**：
  - `AssociatedImageRenderSource` 每个实例都会整读一次关联图像。
  - 每个 `OmeZarrGenerator` 的渲染缓存最多占用约 `RenderCacheSize × ChunkSize² × 4` 字节：默认 16 MiB，ChunkSize 为 8192 时是 4 GiB。切片缓存容量和工具的 `--chunk` 参数都要按这个上限来定。
- **OME-Zarr key 的长度**：`TryGetEntry` 会先对任意长度的 key 做 `Split`，然后才拒绝。端点或生成器应先检查长度，合法 key 不超过 50 个字符。
- **计划 3 动工前考虑补充的 API**，以免之后破坏兼容：
  - 写入调用方缓冲区的 `GetTile(..., Span<byte>)`。
  - 基于 pinned `SKPixmap` 的零拷贝编码。现在 `EncodeCore` 要经过 SKBitmap 和 SKImage 复制两次，预乘路径还多一次 Clone。
  - `OmeZarrGenerator.ContainsKey`：HEAD 请求和 404 判断时不必真的渲染。
  - 把 chunk 写入 `Stream` 或 `IBufferWriter`。
- **命名空间遮蔽（R11）**：在 `OpenSlideSharp.*` 命名空间里写 `SkiaSharp.SKBitmap` 这样的限定名会报 CS0234。解决办法是在文件顶部写 `using SkiaSharp;`，或者改用 `global::SkiaSharp`。
- **关联图像的 OME-Zarr 继承了 `LimitBounds`**：结果是元数据里多了一个 `translation: [0,0,0]`，`ToString` 显示 `limitBounds: True`。DeepZoom 对关联图像强制使用 false，应保持一致。

## 性能（计划 3 做服务端时一并评估）

- `DeepZoomGenerator.GetTile` 对每个瓦片调用了两次 `SlideGeometry.Map`。
- `LanczosResampler`：
  - 每次调用都重新计算系数；
  - 中间缓冲区没有池化；
  - 极端尺寸下，`rows × width × 4` 应先检查再分配。
- `PngChunks.BuildRgbPng` 给 `MemoryStream` 预分配的容量偏小，约差 70 字节，导致 512×512 的第 2 级转换都要扩容一次。可以直接用 `idat.Length + iccpData.Length + 69`。

## 计划 5（发布）之前必须处理

- **依赖固定**：
  - `SkiaSharp` 固定在 3.119.4（R3），因为 Avalonia 12 依赖 3.x。Avalonia 升级到 4.x 后再一起评估。
  - `JsonSchema.Net` 固定在 8.0.5（R8），9.x 改用 OSMF EULA，不要升级。
- **canary 测试** `SkiaCodec_RefusedLutProfiles_DisagreeWithLittleCms`：升级 SkiaSharp 后如果它失败，说明 skcms 的行为变了，要重新评估第 2 级的白名单（R14）。
- **`Test-Packages.ps1`**：依赖组检查写的是 `-lt 3`，也不校验 TFM 名称。
- **CI**：
  - omezarr-interop job 重跑了全部测试；
  - 没有 pip 缓存；
  - `eng/requirements-interop.txt` 只固定了版本，没有锁哈希。这属于发布供应链问题。
- **转换器缓存**：以 ICC 数组的引用作为键。调用方通过 `PixelImage` 构造函数传入 profile 后，如果又修改了这个数组，缓存就会过期。需要在文档里说明，或者改为复制后再用作键。
- **参数校验**：
  - `DeepZoomGenerator` 的 `tileSize + 2*overlap ≤ int.MaxValue/4` 会拒绝 Python 能接受的参数，而且 `ParamName` 报的是 overlap。改为在 `GetTile` 中做 `CheckedPixelBytes` 更合适。
  - `GetThumbnail` 先分配输出、后检查层级大小；超限时报的 `ParamName`（width / maxWidth）也不准确。

## 可以继续延后（低优先级）

- **重复代码**：
  - 宽高和目标长度的校验在 `AssociatedImageRenderSource.ReadRegion`、`RegionRenderer.OutputSpan`、`OpenSlide.ReadRegion` 三处重复；
  - Skia 转换器两个 `Apply` 的参数校验前导代码重复；
  - 有限性检查在三处重复（netstandard2.0 没有 `double.IsFinite`）。
- **`Compositor.CompositeInPlace`**：长度不是 4 的倍数时会忽略尾部字节。目前只有内部调用，调用方都传 4 的倍数。
- **测试缺口**：
  - `PixelImage` 构造函数的校验（未定义的 format、`width*4` 溢出）；
  - 第 2 级并发、转换器缓存行为、预乘图像 + ICC + Default 的 PNG、APP2 位于 APP0 之后、PNG CRC 的已知向量；
  - resample oracle 的条目数没有固定；非法参数（源尺寸、高度）的测试；`OracleData` 接受 `IncompleteInput`；
  - 关联图像首次读取并发、共享 `RegionRenderer` 并发；缩略图测试没有用 oracle 的 `level`；
  - `GetTile` 的释放检查没有能单独失败的回归测试；exact 瓦片没有断言 alpha 为 255；`DeepZoomOracle` 的注释过时；
  - 并发请求同一区域只渲染一次（换成 `PublicationOnly` 现有测试也能通过）；超大 mpp 的回退；netstandard 上 gzip 级别的映射；
  - `verify-omezarr.py` 遇到空 manifest 会输出 “OK: 0 stores”。

## 计划 2 中做出的裁定（摘要）

- **R1**：几何测试只对 overlap 为 0 的 oracle 条目检查 `Map`，overlap 的处理由 DeepZoom 的公开 API 验证。
- **R2**：OME-Zarr 不记录日志，由调用方负责告警。
- **R3**：SkiaSharp 固定在 3.119.4。
- **R4**：bounds 的宽或高为负时视为缺失；为 0 时按解析结果保留。
- **R5**：缩放按 Pillow 13 的规则，高度缩小远多于宽度时先做垂直方向，由 oracle 覆盖。
- **R6**：NGFF schema 的许可写的是 W3C Software and Document License 加 Apache-2.0。
- **R7**：只有 mpp-x 和 mpp-y 都有效时才使用物理单位；选项在 setter 中校验。
- **R8**：JsonSchema.Net 固定在 8.0.5，因为 9.x 是 OSMF EULA。
- **R9**：本文件在终审之后再写。
- **R10**：终审范围限定为计划 2 的提交区间。
- **R11**：保留 `OpenSlideSharp.SkiaSharp` 命名空间。
- **R12**：保留 `NgffVersion` 的枚举顺序。
- **R13 / R14**：第 2 级 ICC 转换只接受 A2B0 为 mAB、PCS 为 Lab 的 profile。原因是对照 LittleCMS 实测：XYZ PCS 的 A2B 亮度减半，Lab PCS 的 lut16 误差可达 7。

## 已知问题（维护者决定只写文档）

Windows 上遇到损坏的 JPEG/PNG 会触发 CFG/longjmp 崩溃，详见 `docs/known-issues/windows-cfg-longjmp-crash.md`。
