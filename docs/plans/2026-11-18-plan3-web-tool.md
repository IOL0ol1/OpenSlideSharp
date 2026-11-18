# Plan 3：ASP.NET Core 端点、Web 示例与命令行工具 Implementation Plan

**Goal:** 交付以下三项：
- 可嵌入任意 ASP.NET Core 应用的 `OpenSlideSharp.AspNetCore`，提供 Deep Zoom 与 OME-Zarr 端点，以及切片缓存；
- 合并 openslide-python `deepzoom_server.py` 与 `deepzoom_multiserver.py` 的示例 `samples/OpenSlideSharp.Web`；
- 对标 `deepzoom_tile.py` 的 dotnet tool `OpenSlideSharp.Tool`（命令 `openslide-sharp`，子命令 `deepzoom` 和 `omezarr`）。

**Architecture:**
- **切片缓存。** 一个单例 `SlideCache` 按文件完整路径缓存 `SlideEntry`。每个条目包含：
  - 打开的 `OpenSlide`；
  - 按颜色模式准备好的 `IColorTransform`；
  - 主图与各关联图像按需创建的 `DeepZoomGenerator` 和 `OmeZarrGenerator`。
- **淘汰与租约。** 缓存采用 LRU 淘汰。请求通过租约（lease）使用条目，被淘汰时等最后一个租约归还后再释放。
- **端点。** 用一个 catch-all 路由接收请求，手工解析路径，再交给 `ISlideResolver` 映射到文件。
- **示例与工具。** 示例应用只负责页面。工具在进程内并行渲染并写文件。

**Tech Stack:**
- .NET 10、ASP.NET Core 10（`Microsoft.AspNetCore.App` 框架引用）
- 测试：xUnit v3、`Microsoft.AspNetCore.TestHost` 10.0.15
- 命令行：System.CommandLine 3.0.0
- 前端：OpenSeadragon 6.1.1（BSD-3-Clause）、openseadragon-scalebar（NIST，公有领域）
- CI 互操作：Python zarr / ome-zarr / fsspec / aiohttp

**Spec:** `docs/design/2026-11-17-openslidesharp-v4-refactor-design.md`（§6.4、§6.5、§6.6、§8）。路线图见 `docs/plans/2026-11-17-openslidesharp-v4-roadmap.md`；遗留事项见 `docs/notes/2026-11-17-plan1-carryover.md` 和 `docs/notes/2026-11-17-plan2-carryover.md`。

## Global Constraints

- **目标框架与依赖：**
  - `OpenSlideSharp.AspNetCore`、`OpenSlideSharp.Tool`、`samples/OpenSlideSharp.Web` 以及它们的测试项目，都只针对 `net10.0`。
  - `OpenSlideSharp.AspNetCore` 依赖 `OpenSlideSharp`、`OpenSlideSharp.SkiaSharp`（同版本）和 `Microsoft.AspNetCore.App` 框架引用，**不**依赖 `OpenSlideSharp.runtime`。
  - 工具包内必须带上全部 5 个 RID 的原生库；示例在仓库内用 `eng/OpenSlideNative.targets`。
- **默认值，与 Python 示例一致：**

  | 选项 | 默认值 |
  |---|---|
  | `TileSize` | 254 |
  | `Overlap` | 1 |
  | `Format` | `jpeg` |
  | `Quality` | 服务端 75，工具 90 |
  | `LimitBounds` | `true`（Python `--ignore-bounds` 取反） |
  | `ColorMode` | `Default` |
  | `SlideCacheSize` | 10 |

  OME-Zarr 默认值沿用 `OmeZarrOptions`：NGFF 0.5、chunk 512、不压缩。
- **颜色模式：**
  - **Default：** 对每个图像（切片和每张关联图像各自的 ICC）调用 `SkiaColorTransform.Create`。
    - 得到转换器时，交给生成器在渲染阶段转换，编码时用 `ColorMode.Ignore`。
    - 返回 null 时，DeepZoom 改用 `ColorMode.Embed`，OME-Zarr 不做转换；每个图像只记录一次 warning。
  - **Embed：** 生成器不转换，编码时用 Embed。OME-Zarr 无法嵌入 ICC，按 Ignore 处理，每个图像只记录一次 warning。
  - **Ignore：** 不转换，丢弃 ICC。
  - **没有 ICC 的图像：** 三种模式行为相同，不记录日志。
- **URL 形式：**
  - **DeepZoom**（prefix 由 `MapDeepZoom` 指定）：
    - `{prefix}/{slide}.dzi`
    - `{prefix}/{slide}_files/{level}/{col}_{row}.{jpeg|png}`
    - 关联图像：`{prefix}/{slide}/associated/{name}.dzi` 与 `{prefix}/{slide}/associated/{name}_files/{level}/{col}_{row}.{fmt}`
  - **OME-Zarr：**
    - `{prefix}/{slide}.zarr/{key}`
    - 关联图像：`{prefix}/{slide}/associated/{name}.zarr/{key}`
  - **`{slide}` 的取值：**
    - 单文件模式下是 resolver 给出的固定名字；
    - 目录模式下是相对根目录、以 `/` 分隔的路径。
  - **`{name}` 的编码：** 关联图像名称按 URL 段编码（`Uri.EscapeDataString`）。
- **状态码：**
  - **404：** 切片或关联图像不存在、路径穿越、文件不能被 OpenSlide 识别、层级或地址越界、tile 格式不是 jpeg/png、OME key 不存在（`.zmetadata` 等探测请求也算在内）。
  - **500：** 切片进入 OpenSlide 粘滞错误状态。返回 500，并把该条目从缓存中逐出，下次请求重新打开。
- **Content-Type：**

  | 内容 | Content-Type |
  |---|---|
  | DZI | `application/xml` |
  | 瓦片 | `image/jpeg` / `image/png` |
  | OME 元数据（`zarr.json`、`.zattrs`、`.zgroup`、`.zarray`） | `application/json` |
  | chunk | `application/octet-stream` |

- **OME-Zarr 端点：**
  - 支持 GET 和 HEAD。HEAD 不渲染 chunk，只在 key 合法且存在时返回 200 和 Content-Type。
  - `AllowCrossOrigin` 默认为 true：响应带 `Access-Control-Allow-Origin: *`；对 `OPTIONS` 预检返回 204，并带 `Access-Control-Allow-Methods: GET, HEAD, OPTIONS`。
- **安全：**
  - 目录模式只允许解析到根目录内部：规范化后的完整路径必须以 `root + 分隔符` 开头，并拒绝符号链接逃逸（`FileInfo.LinkTarget` 不为 null 时，按解析后的目标再检查一次）。
  - URL 中出现 `..`、空段、反斜杠、驱动器号或冒号时一律 404。
  - OME key 长度超过 256 时直接 404，不再解析。
- **原生库依赖：** 工具和示例直接依赖原生库：工具通过 `OpenSlideSharp.runtime` 包，示例通过仓库内的 `eng/OpenSlideNative.targets`。
- **提交规范：**
  - 提交身份为 `IOL0ol1 <1353839731@qq.com>`（local config 已设置）。
  - `src/**` 项目开启 `TreatWarningsAsErrors`，并为公开成员生成 XML 文档。
- **命名空间与引用：**
  - 新代码位于 `OpenSlideSharp.*` 命名空间。引用 Skia 类型时用文件顶部的 `using SkiaSharp;`，不要写 `SkiaSharp.X` 这样的限定名（会被 `OpenSlideSharp.SkiaSharp` 命名空间遮蔽）。
  - `SkiaSharp` 固定在 3.119.4，`JsonSchema.Net` 固定在 8.0.5，不得升级。
- **Windows 上的已知崩溃：** 不读 `unreadable.svs` 的像素，沿用 `Assert.SkipWhen` 写法。

## Review Focus

1. **多个请求同时访问一个正在被淘汰的切片**（缓存容量 1，两个切片交替请求，并发 32 个）：不得出现 `ObjectDisposedException` 或 500，所有响应都应正确。→ Task 1 `SlideCache_EvictionDuringLease_DefersDispose`、Task 2 `Tiles_ConcurrentAcrossEvictions_AllSucceed`。
2. **目录模式下的路径穿越**：`../`、`..%2F`、`%2e%2e/`、绝对路径、`C:`、反斜杠、指向根目录外的符号链接，都只能得到 404，不能读出根目录外的文件，也不能泄露存在性（404 与“文件不存在”无法区分）。→ Task 1 `DirectoryResolver_RejectsTraversal`、Task 2 `Dzi_TraversalAttempts_Return404`。
3. **同一切片首次被大量并发请求**：只打开一次（`OpenSlide.Open` 只调用一次）；打开失败时所有等待者都得到 404，失败结果不被缓存。→ Task 1 `SlideCache_ConcurrentFirstAccess_OpensOnce`、`SlideCache_OpenFailure_NotCached`。
4. **vizarr 一类的 Web 查看器跨域读取 OME-Zarr**：先 `OPTIONS`、再 `HEAD`/`GET`，元数据和 chunk 都能拿到，且带 CORS 头；`.zmetadata` 探测得到 404 而不是 500。→ Task 3 `OmeZarr_CorsPreflightAndHead`、`OmeZarr_UnknownKeys_Return404`。
5. **工具导出大切片时被 Ctrl+C 中断，或输出目录已存在**：已有的同名输出被覆盖，不残留半个文件。中断时以非零退出码结束，不留下长度为 0 的瓦片。→ Task 5 `DeepZoom_ExistingOutput_IsReplaced`、`Export_Cancellation_LeavesNoPartialFiles`。

---

### Task 1: `OpenSlideSharp.AspNetCore`：resolver、选项与切片缓存

**Files:**
- Create:
  - `src/OpenSlideSharp.AspNetCore/OpenSlideSharp.AspNetCore.csproj`（`net10.0`）：
    - `FrameworkReference Microsoft.AspNetCore.App`；
    - ProjectReference 指向核心项目与 Skia 项目；
    - `PackageId=OpenSlideSharp.AspNetCore`；
    - Description：`ASP.NET Core endpoints serving OpenSlide slides as Deep Zoom and OME-Zarr, with a slide cache.`；
    - Tags：`openslide wsi pathology deepzoom ome-zarr aspnetcore`。
  - `src/OpenSlideSharp.AspNetCore/OpenSlideServerOptions.cs`
  - `src/OpenSlideSharp.AspNetCore/ISlideResolver.cs`、`SingleSlideResolver.cs`、`DirectorySlideResolver.cs`
  - `src/OpenSlideSharp.AspNetCore/SlideCache.cs`、`SlideEntry.cs`、`SlideLease.cs`
  - `src/OpenSlideSharp.AspNetCore/OpenSlideServiceCollectionExtensions.cs`
  - `src/OpenSlideSharp.AspNetCore/README.md`
  - `test/OpenSlideSharp.AspNetCore.Tests/OpenSlideSharp.AspNetCore.Tests.csproj`（net10.0，xUnit v3 + MTP，引用 `Microsoft.AspNetCore.TestHost`，导入 `eng/OpenSlideNative.targets`，复制 `../OpenSlideSharp.Tests/Fixtures/**` 到输出目录的 `Fixtures/`）
  - `test/OpenSlideSharp.AspNetCore.Tests/ResolverTests.cs`、`SlideCacheTests.cs`
- Modify: `OpenSlideSharp.slnx`、`Directory.Packages.props`（`Microsoft.AspNetCore.TestHost` 10.0.15）

**Interfaces:**
- Produces（公开，命名空间 `OpenSlideSharp.AspNetCore`）：

```csharp
public sealed class OpenSlideServerOptions
{
    public int SlideCacheSize { get; set; } = 10;                 // >= 1
    public ColorMode ColorMode { get; set; } = ColorMode.Default; // OpenSlideSharp.SkiaSharp.ColorMode
    public DeepZoomServerOptions DeepZoom { get; } = new();
    public OmeZarrServerOptions OmeZarr { get; } = new();
}
public sealed class DeepZoomServerOptions
{
    public int TileSize { get; set; } = 254;
    public int Overlap { get; set; } = 1;
    public ImageFormat Format { get; set; } = ImageFormat.Jpeg;   // DZI 中写的格式；URL 中 jpeg/png 都接受
    public int Quality { get; set; } = 75;
    public bool LimitBounds { get; set; } = true;
}
public sealed class OmeZarrServerOptions
{
    public NgffVersion Version { get; set; } = NgffVersion.V0_5;
    public int ChunkSize { get; set; } = 512;
    public OmeZarrCompression Compression { get; set; } = OmeZarrCompression.None;
    public int GzipLevel { get; set; } = 6;
    public bool LimitBounds { get; set; } = true;
    public bool AllowCrossOrigin { get; set; } = true;
}

public interface ISlideResolver
{
    // urlPath：DeepZoom/OME 前缀之后的 {slide} 部分（已解码、以 '/' 分隔）
    bool TryResolve(string urlPath, [NotNullWhen(true)] out string? filePath);
}
public sealed class SingleSlideResolver : ISlideResolver
{
    public SingleSlideResolver(string filePath, string name = "slide");
    public string Name { get; }
}
public sealed class DirectorySlideResolver : ISlideResolver
{
    public DirectorySlideResolver(string rootDirectory);
    public string RootDirectory { get; }        // 规范化后的完整路径
}

public static class OpenSlideServiceCollectionExtensions
{
    public static IServiceCollection AddOpenSlide(this IServiceCollection services, Action<OpenSlideServerOptions>? configure = null);
    public static IServiceCollection AddOpenSlide(this IServiceCollection services, IConfiguration configuration); // 绑定 OpenSlideServerOptions
}
```

- Produces（公开，供端点与示例使用）：

```csharp
public sealed class SlideCache : IDisposable
{
    public SlideCache(IOptions<OpenSlideServerOptions> options, ILogger<SlideCache> logger);
    public SlideLease? TryAcquire(string filePath);  // 打不开、无法识别或文件不存在时返回 null（并记录 debug 日志）
    public void Evict(string filePath);              // 粘滞错误后调用
    public int Count { get; }
}
public sealed class SlideLease : IDisposable          // Dispose 时归还租约；不能 Dispose 第二次
{
    public SlideEntry Entry { get; }
}
public sealed class SlideEntry
{
    public string FilePath { get; }
    public OpenSlide Slide { get; }
    public DeepZoomGenerator GetDeepZoom(string? associatedName);  // null 表示主图；名字不存在时抛 KeyNotFoundException
    public OmeZarrGenerator GetOmeZarr(string? associatedName);
    public ColorMode EncodeMode(string? associatedName);          // 按 Global Constraints 的颜色规则得出编码时用的模式
}
```

- 缓存语义：
  - 以 `Path.GetFullPath` 作为键，Windows 上不区分大小写。
  - 首次访问用 `Lazy<SlideEntry?>`（ExecutionAndPublication），保证同一路径只打开一次；打开失败的条目立即移除。
  - 容量满时淘汰最久未使用的条目。被淘汰的条目在租约计数归零时才 `Dispose` slide。
  - 生成器和颜色转换器在条目内按图像懒创建并缓存（`ConcurrentDictionary`）。`DeepZoomGenerator` 中关联图像固定使用 `limitBounds: false`；`OmeZarrGenerator` 中关联图像使用 `LimitBounds = false` 的 options 副本。
  - 颜色转换器按图像各自的 ICC 调用 `SkiaColorTransform.Create`。warning 记录的内容为文件名、图像名，以及原因（无法转换，或 OME 不支持 Embed）。
  - `SlideCache.Dispose` 释放所有条目。
- 选项校验：`SlideCacheSize < 1`、`TileSize < 1`、`Overlap < 0`、`Quality` 不在 0–100、`ChunkSize` 不在 16–8192、`GzipLevel` 不在 0–9 时，都在 `AddOpenSlide` 注册的 `IValidateOptions` 中报错（首次解析 options 时抛 `OptionsValidationException`）。

- [ ] **Step 1: 写失败的测试**

`ResolverTests`：
- `SingleResolver_MatchesOnlyItsName`：`"slide"` 能解析，`"other"`、`""`、`"slide/x"` 不能。
- `DirectoryResolver_ResolvesNestedFile`：在临时目录建 `a/b/boxes.tiff`（从 Fixtures 复制），`"a/b/boxes.tiff"` 解析为它的完整路径。
- `DirectoryResolver_RejectsTraversal`（Theory）：`"../x"`、`"a/../../x"`、`"/etc/passwd"`、`"C:/Windows/win.ini"`、`"a\\b"`、`"a//b"`、`"a/./b"`、`""`、`"a/"`，以及一个指向根目录外文件的符号链接（无权创建符号链接时用 `Assert.Skip` 跳过该用例），全部返回 false。

`SlideCacheTests`：
- `SlideCache_SameFile_ReturnsSameEntry`：同一个路径两次 `TryAcquire` 得到同一个 `Entry`。
- `SlideCache_ConcurrentFirstAccess_OpensOnce`：64 个任务同时 `TryAcquire(boxes.tiff)`，所有租约的 `Entry` 是同一实例。用内部钩子 `SlideCache.OpenCount` 断言值为 1（`internal`，测试项目通过 InternalsVisibleTo 访问）。
- `SlideCache_OpenFailure_NotCached`：`TryAcquire` 一个不存在的路径返回 null，`Count == 0`；`unopenable.tiff` 返回 null。
- `SlideCache_EvictionDuringLease_DefersDispose`：容量为 1。先取 A 的租约，再 `TryAcquire(B)` 淘汰 A；此时 A 的租约仍可 `ReadRegion`；归还后 A 的 slide 已 Dispose（`ReadRegion` 抛 `ObjectDisposedException`）。
- `SlideCache_Evict_ReopensNextTime`：`Evict` 之后，`TryAcquire` 得到一个新的 `Entry`。
- `Entry_ColorMode_DefaultWithIcc_UsesTransform`：
  - `boxes.tiff`（ICC 是 588 字节的 RGB 矩阵 profile）在 Default 下，`EncodeMode(null) == ColorMode.Ignore`，且 `GetDeepZoom(null).GetTile(...)` 的 `IccProfile.IsEmpty`；
  - Embed 下 `EncodeMode(null) == ColorMode.Embed`，瓦片带 588 字节 ICC。
- `Entry_AssociatedImage_UsesLimitBoundsFalse`：`small.svs` 的 thumbnail，`GetOmeZarr("thumbnail")` 的 `ToString()` 含 `limitBounds: False`。
- `Options_Invalid_ThrowOnResolve`：`SlideCacheSize = 0` 时，解析 `IOptions<OpenSlideServerOptions>.Value` 抛 `OptionsValidationException`。

- [ ] **Step 2: 运行，确认失败（编译错误）**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**

Run: `dotnet test OpenSlideSharp.slnx -c Release`

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add OpenSlideSharp.AspNetCore resolvers, options and slide cache"
```

---

### Task 2: Deep Zoom 端点

**Files:**
- Create:
  - `src/OpenSlideSharp.AspNetCore/DeepZoomEndpoints.cs`（`MapDeepZoom`）
  - `src/OpenSlideSharp.AspNetCore/Internal/RequestPath.cs`（路径解析）
  - `test/OpenSlideSharp.AspNetCore.Tests/TestApp.cs`（用 TestServer 构建最小应用的辅助类）
  - `test/OpenSlideSharp.AspNetCore.Tests/DeepZoomEndpointTests.cs`
- Modify: `src/OpenSlideSharp.SkiaSharp/PixelImageExtensions.cs`（遗留事项：Default 永不抛异常）

**Interfaces:**
- Consumes: Task 1 的 `SlideCache`、`SlideEntry`、`ISlideResolver`、`OpenSlideServerOptions`。
- Produces：

```csharp
public static class DeepZoomEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapDeepZoom(this IEndpointRouteBuilder endpoints, string prefix, ISlideResolver resolver);
}
```

- 路由：`{prefix}/{**path}`，只接受 GET 和 HEAD。`RequestPath` 按下面的顺序解析：
  1. 以 `.dzi` 结尾：去掉后缀得到 `slidePart`。
  2. 否则查找最后一个 `_files/`：其后必须是 `{level}/{col}_{row}.{fmt}`，三个数字都是非负十进制、不超过 int 范围，`fmt` 为 `jpeg` 或 `png`（区分大小写）；其前是 `slidePart`。
  3. `slidePart` 若含 `/associated/`，在最后一次出现处切开：前半是 slide，后半是关联图像名（不得含 `/`）。
  4. 其他形式一律 404。
- 处理流程：
  1. `resolver.TryResolve`；
  2. `cache.TryAcquire`；
  3. 取生成器，校验层级、列、行（越界时 404）；
  4. `GetTile`；
  5. `Encode(fmt, quality, entry.EncodeMode(name))`。
- 遇到 `OpenSlideException` 时：记录 error 日志，调用 `cache.Evict`，返回 500。
- `PixelImageExtensions.Encode` 的 Default 分支要捕获第 1、2 级 `Apply` 抛出的 `InvalidOperationException`，退化为 Embed（plan 2 遗留事项）。同时把 Skia README 颜色模式表中 Default 行的 “Output ICC” 改为 “none (embedded when the profile cannot be converted)”。

- [ ] **Step 1: 写失败的测试**

`TestApp`：
- `TestApp.Create(ISlideResolver resolver, Action<OpenSlideServerOptions>? configure = null)` 返回一个已启动的 `WebApplication`（`UseTestServer`）和 `HttpClient`；
- 该应用只做 `AddOpenSlide` + `MapDeepZoom("/dz", resolver)` + `MapOmeZarr("/zarr", resolver)`。在 Task 3 实现之前，`MapOmeZarr` 的调用用 `#if` 或分两个 helper，由实现者决定。

`DeepZoomEndpointTests`（`SingleSlideResolver(boxes.tiff, "boxes")`）：
- `Dzi_ReturnsXml`：`GET /dz/boxes.dzi` 返回 200，`Content-Type` 为 `application/xml`，正文等于 `new DeepZoomGenerator(slide, 254, 1, limitBounds: true).GetDzi("jpeg")`。
- `Tile_ReturnsDecodableJpeg`：`GET /dz/boxes_files/9/0/0.jpeg` 返回 200、`image/jpeg`，能用 SKCodec 解码，尺寸等于 `GetTileDimensions(9, 0, 0)`。
- `Tile_Png_Works`：`.png` 返回 200、`image/png`。
- `Tile_OutOfRange_Returns404`（Theory）：`/dz/boxes_files/10/0/0.jpeg`、`/dz/boxes_files/9/5/0.jpeg`、`/dz/boxes_files/9/0/0.gif`、`/dz/boxes_files/9/-1/0.jpeg`、`/dz/boxes_files/9/00/0.jpeg`、`/dz/boxes_files/x/0/0.jpeg`、`/dz/other.dzi`。
- `Associated_DziAndTile`：用 `small.svs` 的单文件 resolver（名字为 `small`）。
  - `GET /dz/small/associated/thumbnail.dzi` 返回 200；
  - 它的最高层 0/0 瓦片返回 200；
  - `/dz/small/associated/nope.dzi` 返回 404。
- `Dzi_TraversalAttempts_Return404`：用 `DirectorySlideResolver(临时目录)`，其中只有 `sub/boxes.tiff`。
  - `/dz/sub/boxes.tiff.dzi` 返回 200；
  - 以下请求都返回 404：`/dz/..%2Fboxes.tiff.dzi`、`/dz/%2e%2e/x.dzi`、`/dz/sub/../sub/boxes.tiff.dzi`、`/dz/sub%5Cboxes.tiff.dzi`、`/dz/C:/x.dzi`。
- `Tiles_ConcurrentAcrossEvictions_AllSucceed`：`SlideCacheSize = 1`，目录里有 `boxes.tiff` 和 `boxes_0.dcm` 两个切片。两者的瓦片交替请求，共 200 次，最多 32 个并发。全部返回 200，且解码尺寸正确。
- `ColorMode_Embed_TileCarriesIcc`：`ColorMode.Embed` 时，`boxes.tiff` 瓦片的 JPEG 含 APP2 `ICC_PROFILE`；Default 时不含。
- `Head_Tile_ReturnsHeadersWithoutBody`。

另外，在 `test/OpenSlideSharp.Tests/Skia/EncodeTests.cs` 中增加 `Encode_DefaultWhenTransformThrows_FallsBackToEmbed`：用一个 `Apply` 抛 `InvalidOperationException` 的测试转换器，经内部注入点（`internal` 的 transform 工厂重载）验证结果为 Embed。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: serve Deep Zoom DZI and tiles from ASP.NET Core"
```

---

### Task 3: OME-Zarr 端点

**Files:**
- Create: `src/OpenSlideSharp.AspNetCore/OmeZarrEndpoints.cs`、`test/OpenSlideSharp.AspNetCore.Tests/OmeZarrEndpointTests.cs`
- Modify: `src/OpenSlideSharp/OmeZarrGenerator.cs`（新增 `ContainsKey`）、`test/OpenSlideSharp.Tests/OmeZarrTests.cs`

**Interfaces:**
- Consumes: Task 1、Task 2 的 `RequestPath`。
- Produces：
  - `public static IEndpointConventionBuilder MapOmeZarr(this IEndpointRouteBuilder endpoints, string prefix, ISlideResolver resolver)`
  - 核心新增 `public bool OmeZarrGenerator.ContainsKey(string key)`：判定规则与 `TryGetEntry` 完全相同，但不渲染、不分配 chunk；不抛异常；key 长度超过 256 时返回 false。`TryGetEntry` 同样先做长度检查。
- 路由：`{prefix}/{**path}`，接受 GET、HEAD、OPTIONS。解析规则：
  - 在第一个 `.zarr/` 处切开，或者路径以 `.zarr` 结尾（此时 key 为空，返回 404）；
  - 前半部分按 Task 2 的方式拆出 slide 和关联图像名，后半部分是 key。
- 处理：
  - GET：用 `TryGetEntry` 取内容；
  - HEAD：用 `ContainsKey` 判断存在，返回 200 和 Content-Type，不带正文；
  - OPTIONS：返回 204，带 CORS 头。
- `AllowCrossOrigin` 为 true 时，所有响应（包括 404）都带 `Access-Control-Allow-Origin: *`。
- 粘滞错误的处理同 Task 2。

- [ ] **Step 1: 写失败的测试**

核心 `OmeZarrTests`：
- `ContainsKey_AgreesWithTryGetEntry`：枚举出的每个 key，以及恶意 key 列表中的每一项，`ContainsKey` 与 `TryGetEntry` 的结果一致。用计数源断言 `ContainsKey` 不触发渲染。
- `Keys_LongerThan256_AreRejected`：`new string('0', 257)` 和 `"0/" + new string('/', 300)` 都返回 false。

`OmeZarrEndpointTests`（`SingleSlideResolver(boxes.tiff, "boxes")`，`ChunkSize = 64`）：
- `RootMetadata_V05`：`GET /zarr/boxes.zarr/zarr.json` 返回 200、`application/json`，正文等于同参数生成器的 `TryGetEntry("zarr.json")`。注意服务端的 `LimitBounds` 为 true，生成器要用相同参数。
- `Chunk_MatchesGenerator`：`/zarr/boxes.zarr/0/c/0/0/0` 返回 200、`application/octet-stream`，字节与生成器输出相同。
- `V04_Keys`：`Version = V0_4` 时，`/.zattrs`、`/.zgroup`、`/0/.zarray`、`/0/0/0/0` 都返回 200。
- `OmeZarr_UnknownKeys_Return404`（Theory）：`/zarr/boxes.zarr/.zmetadata`、`/zarr/boxes.zarr/9/zarr.json`、`/zarr/boxes.zarr/`、`/zarr/boxes.zarr`、`/zarr/other.zarr/zarr.json`、`/zarr/boxes.zarr/0/c/0/99/0`。
- `OmeZarr_CorsPreflightAndHead`：
  - `OPTIONS /zarr/boxes.zarr/zarr.json` 返回 204，带 `Access-Control-Allow-Origin: *`，`Allow-Methods` 含 HEAD；
  - `HEAD .../0/c/0/0/0` 返回 200，正文长度为 0，带 CORS 头；
  - `AllowCrossOrigin = false` 时不带 CORS 头。
- `Associated_ThumbnailStore`：用 `small.svs`，`/zarr/small/associated/thumbnail.zarr/zarr.json` 返回 200，其中 multiscales 的 name 为 `thumbnail`。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: serve OME-Zarr stores from ASP.NET Core with HEAD and CORS"
```

---

### Task 4: 示例 `samples/OpenSlideSharp.Web`

**Files:**
- Create:
  - `samples/OpenSlideSharp.Web/OpenSlideSharp.Web.csproj`：`net10.0`，Web SDK；ProjectReference 指向 AspNetCore 项目；导入 `../../eng/OpenSlideNative.targets`；`IsPackable=false`。
  - `samples/OpenSlideSharp.Web/Program.cs`、`Pages/Index.cshtml(.cs)`、`Pages/Slide.cshtml(.cs)`、`Pages/Shared/_Layout.cshtml`（Razor Pages）
  - `samples/OpenSlideSharp.Web/appsettings.json`
  - `samples/OpenSlideSharp.Web/wwwroot/viewer/viewer.js`、`viewer.css`
  - 共享的查看器静态资源 `assets/viewer/`：
    - `openseadragon.min.js`：6.1.1，从 npm `openseadragon@6.1.1` 的 `build/openseadragon/` 取，连同 `images/`；
    - `openseadragon-scalebar.js`：来自 https://github.com/usnistgov/OpenSeadragonScalebar 的提交 `e30716c06939e1dbccaeba7472ba1b98a1f047da`；
    - `licenses/LICENSE.openseadragon`、`licenses/LICENSE.openseadragon-scalebar`：从 openslide-python `examples/deepzoom/licenses/` 取同名文件，并核对是否与当前版本一致。
  - 由示例的 csproj 把 `assets/viewer/**` 链接到 `wwwroot/lib/`（Content Link + CopyToPublishDirectory）。
  - `test/OpenSlideSharp.Web.Tests/OpenSlideSharp.Web.Tests.csproj`（`Microsoft.AspNetCore.Mvc.Testing` 10.0.15）、`WebSampleTests.cs`
  - `samples/OpenSlideSharp.Web/README.md`
- Modify: `OpenSlideSharp.slnx`、`Directory.Packages.props`

**Interfaces:**
- 配置项：
  - `SlidePath`：文件时为单片模式，目录时为目录模式；
  - `OpenSlide` 段：绑定 `OpenSlideServerOptions`。
- 命令行与 Python 选项的对应：

  | 命令行选项 | 配置项 |
  |---|---|
  | `-s` / `--size` | `OpenSlide:DeepZoom:TileSize` |
  | `-e` / `--overlap` | `OpenSlide:DeepZoom:Overlap` |
  | `-f` / `--format` | `OpenSlide:DeepZoom:Format` |
  | `-Q` / `--quality` | `OpenSlide:DeepZoom:Quality` |
  | `--color-mode` | `OpenSlide:ColorMode`，取值 default / embed / ignore |
  | `-l` / `--listen` | 监听主机 |
  | `-p` / `--port` | 端口（与 `-l` 一起组成 `urls`，默认 `http://127.0.0.1:5000`） |
  | `-B` / `--ignore-bounds` | 无值开关，把 `OpenSlide:DeepZoom:LimitBounds` 和 `OpenSlide:OmeZarr:LimitBounds` 设为 false；在 `CreateBuilder` 之前预处理 args |
  | 第一个位置参数 | `SlidePath` |

- 页面：
  - `/`：
    - 单片模式直接显示切片页（等同 Python `deepzoom_server`）；
    - 目录模式递归列出 `OpenSlide.DetectFormat` 不为 null 的文件，以树形链接显示，结果缓存 60 秒（等同 Python `files.html`）。
  - `/slide/{**path}`：切片页，三栏布局与 Python `slide-multipane.html` 相同：
    - 左栏是主图链接和关联图像列表；
    - 中间是 OpenSeadragon，配比例尺，mpp 取 `openslide.mpp-x`/`y` 的平均值，缺失时为 0；
    - 右栏是属性表。
  - 页面底部显示该切片的 OME-Zarr 根地址，以及“Open in vizarr”外链 `https://hms-dbmi.github.io/vizarr/?source=<绝对 URL>`。
- 端点：`app.MapDeepZoom("/dz", resolver)`、`app.MapOmeZarr("/zarr", resolver)`。resolver 的类型由 `SlidePath` 是文件还是目录决定；单片模式下名字固定为 `slide`。
- 前端不使用 jQuery，`viewer.js` 用原生 DOM API；不依赖任何 CDN。

- [ ] **Step 1: 写失败的测试**

`WebSampleTests` 使用 `WebApplicationFactory<Program>`，`Program` 需要声明 `public partial class Program`；通过 `WithWebHostBuilder` 的 `UseSetting("SlidePath", …)` 设定切片路径。
- `SingleMode_IndexShowsViewer`：`SlidePath` 为 `boxes.tiff` 时，`GET /` 返回 200，HTML 含 `/dz/slide.dzi` 和 `openseadragon`。
- `DirectoryMode_ListsSlides`：`SlidePath` 指向临时目录（含 `a/boxes.tiff` 和一个 `notes.txt`），`GET /` 的结果含 `a/boxes.tiff` 链接，不含 `notes.txt`。
- `SlidePage_ShowsPropertiesAssociatedAndOmeZarr`：`small.svs` 的切片页含 `openslide.vendor`、`thumbnail`、`/zarr/slide.zarr/`、`vizarr`。
- `StaticAssets_Served`：`/lib/openseadragon.min.js` 和 `/lib/openseadragon-scalebar.js` 返回 200。
- `CommandLine_MapsPythonOptions`：用 `Program.BuildConfiguration(args)`（internal static，把参数预处理和映射抽出来以便测试），输入 `["-s","510","-e","2","-f","png","-Q","90","--color-mode","ignore","-B","x.svs"]`，断言各配置项的值。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**（含资源下载并校验 sha256；把下载的哈希写进 `assets/viewer/README.md`）
- [ ] **Step 4: 运行全部测试，确认通过**；再手动启动一次 `dotnet run --project samples/OpenSlideSharp.Web -- test/OpenSlideSharp.Tests/Fixtures/boxes.tiff`，用 curl 取 `/`、`/dz/slide.dzi`、一个瓦片，以及 `/zarr/slide.zarr/zarr.json`，确认都是 200。
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add the OpenSlideSharp.Web sample (single slide and directory modes)"
```

---

### Task 5: `OpenSlideSharp.Tool`（`openslide-sharp deepzoom|omezarr`）

**Files:**
- Create:
  - `src/OpenSlideSharp.Tool/OpenSlideSharp.Tool.csproj`：
    - `net10.0`，`OutputType=Exe`，`PackAsTool=true`，`ToolCommandName=openslide-sharp`，`PackageId=OpenSlideSharp.Tool`；
    - Description：`Command-line tool that exports OpenSlide slides as Deep Zoom (like deepzoom_tile.py) or OME-Zarr.`；
    - 引用 System.CommandLine 3.0.0，以及核心与 Skia 两个 ProjectReference；
    - 原生库：`'$(UseOpenSlideRuntimePackage)' == 'true'` 时用 `PackageReference OpenSlideSharp.runtime`（版本为 `$(Version)`），否则导入 `eng/OpenSlideNative.targets`；
    - 把 `assets/viewer/**` 作为 EmbeddedResource，供 `-r` 使用。
  - `src/OpenSlideSharp.Tool/Program.cs`、`DeepZoomCommand.cs`、`OmeZarrCommand.cs`、`Export/TileWriter.cs`（并行与原子写入）、`Export/ViewerWriter.cs`（`-r` 模式的 HTML）
  - `src/OpenSlideSharp.Tool/README.md`
  - `test/OpenSlideSharp.Tool.Tests/OpenSlideSharp.Tool.Tests.csproj`、`DeepZoomCommandTests.cs`、`OmeZarrCommandTests.cs`
- Modify: `OpenSlideSharp.slnx`、`Directory.Packages.props`（`System.CommandLine` 3.0.0）

**Interfaces:**
- `openslide-sharp deepzoom <slide>`：

  | 选项 | 含义 | 默认值 |
  |---|---|---|
  | `-o/--output <name>` | 输出名 | 切片文件名去掉扩展名 |
  | `-s/--size` | 瓦片尺寸 | 254 |
  | `-e/--overlap` | 重叠 | 1 |
  | `-f/--format jpeg\|png` | 格式 | jpeg |
  | `-Q/--quality` | 质量 | 90 |
  | `-B/--ignore-bounds` | 忽略 bounds | 关 |
  | `-c/--color-mode default\|embed\|ignore` | 颜色模式 | default |
  | `-j/--jobs` | 并发数 | `Environment.ProcessorCount` |
  | `-r/--viewer` | 生成查看器 | 关 |

  - 输出：`<name>.dzi` 和 `<name>_files/{level}/{col}_{row}.{fmt}`。
  - 带 `-r` 时输出到目录 `<name>/`，内容为：
    - `slide.dzi` 与 `slide_files/`；
    - 各关联图像 `<slug>.dzi` 与 `<slug>_files/`，其中 slug 按 Python `_slugify` 规则生成：NFKD、小写、去掉非 ASCII，`[^a-z0-9]+` 替换为 `_`；
    - `index.html`：与 Python 的 multipane 页相同；DZI 内容内嵌在页面里，所以 `file://` 也能打开；
    - `static/`：OpenSeadragon、比例尺插件、`images/`、`licenses/`。
- `openslide-sharp omezarr <slide>`：

  | 选项 | 含义 | 默认值 |
  |---|---|---|
  | `-o/--output <dir>` | 输出目录 | `<stem>.zarr` |
  | `--ngff 0.5\|0.4` | NGFF 版本 | 0.5 |
  | `--chunk` | chunk 尺寸 | 512 |
  | `--compression none\|gzip` | 压缩 | none |
  | `--gzip-level` | gzip 级别 | 6 |
  | `-B/--ignore-bounds` | 忽略 bounds | 关 |
  | `-c/--color-mode default\|ignore` | 颜色模式 | default |
  | `-j/--jobs` | 并发数 | — |
  | `--associated` | 同时导出关联图像 | 关 |

  - 输出：遍历 `EnumerateKeys()`，把 key 中的 `/` 映射为目录写成文件。
  - 带 `--associated` 时，关联图像另写到 `<output>/../<stem>_associated/<slug>.zarr`。
- 公共行为：
  - 进度输出到 stderr，每秒最多一行，格式为 `Tiling <name>: <done>/<total> tiles`，与 Python 一致。
  - 成功时退出码为 0。参数错误为 2（System.CommandLine 默认）；切片打不开为 1，并输出消息；被取消（Ctrl+C / `CancellationToken`）为 130。
  - 文件先写到同目录的 `.<name>.tmp` 再 `File.Move(overwrite: true)`，保证不留下半个文件。
  - 已存在的 `<name>_files/`（或 zarr 目录）在开始时整体删除；`-r` 模式下删除整个 `<name>/` 目录。
  - 并发：每个工作线程依次处理，同一切片只打开一次（OpenSlide 是线程安全的）；生成器在所有线程间共享。
  - 颜色规则与服务端相同；warning 输出到 stderr。
- 为便于测试，命令处理放在 `internal static Task<int> DeepZoomCommand.RunAsync(DeepZoomArguments args, TextWriter stderr, CancellationToken ct)` 这类方法中，`Program` 只负责解析参数。`OmeZarrCommand` 同理。

- [ ] **Step 1: 写失败的测试**（输出到临时目录）

`DeepZoomCommandTests`：
- `DeepZoom_Boxes_WritesAllTiles`：`boxes.tiff`，参数 `-s 254 -e 1`。
  - 写出 `boxes.dzi`，内容等于 `DeepZoomGenerator(slide, 254, 1, limitBounds: true).GetDzi("jpeg")`；
  - `boxes_files` 下的文件总数等于 `TileCount`（11）；
  - 每层目录名为 0–9；
  - 随机抽一个瓦片，能用 SKCodec 解码，尺寸正确。
- `DeepZoom_Png_UsesPngExtension`。
- `DeepZoom_Viewer_WritesIndexAndStatic`：`small.svs -r`，生成 `small/index.html`（含 `slide.dzi`、`thumbnail.dzi`、属性名 `openslide.vendor`）、`small/static/openseadragon.min.js` 和 `small/thumbnail_files/`。
- `DeepZoom_ExistingOutput_IsReplaced`：预先建一个 `boxes_files/99/stale.jpeg`，运行后该文件不存在。
- `Export_Cancellation_LeavesNoPartialFiles`：传入很快就取消的 token（第一次写入后取消），返回 130；输出目录里没有 `.tmp` 文件，也没有长度为 0 的瓦片。
- `DeepZoom_UnopenableSlide_Returns1`：`unopenable.tiff` 返回 1，stderr 含文件名。

`OmeZarrCommandTests`：
- `OmeZarr_Boxes_WritesEveryKey`：`--chunk 64 --ngff 0.4 --compression gzip`。按 `OmeZarrGenerator.EnumerateKeys()` 列出的每个 key 都有对应文件，内容逐字节相同。
- `OmeZarr_Associated_WritesThumbnailStore`：`small.svs --associated` 生成 `small_associated/thumbnail.zarr/zarr.json`。
- `Program_ParsesArguments`：`Program.Main(["deepzoom","--help"])` 返回 0；`["deepzoom"]`（缺少参数）返回 2。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add the openslide-sharp tool with deepzoom and omezarr exports"
```

---

### Task 6: 打包、互操作 CI 与文档

**Files:**
- Modify:
  - `eng/Test-Packages.ps1`、`test/PackageSmoke/*`
  - `.github/workflows/ci.yml`
  - `eng/requirements-interop.txt`（追加 `fsspec==2026.9.0`、`aiohttp==3.14.5`）、`eng/verify-omezarr.py`
  - `README.md`、`src/OpenSlideSharp.AspNetCore/README.md`、`src/OpenSlideSharp.Tool/README.md`、`samples/OpenSlideSharp.Web/README.md`
- Create: `docs/notes/2026-11-18-plan3-carryover.md`（在最终审阅之后写，与计划 2 的做法相同）

**Interfaces:**
- `Test-Packages.ps1`：
  - 打包新增 `OpenSlideSharp.AspNetCore` 与 `OpenSlideSharp.Tool`。工具打包的顺序：先把 core、Skia、runtime 打进本地 feed，再以 `-p:UseOpenSlideRuntimePackage=true` 打包工具，并把本地 feed 加进还原源。
  - 断言 AspNetCore 的 nuspec：只有一个 `net10.0` 组，依赖 `OpenSlideSharp` 与 `OpenSlideSharp.SkiaSharp`（版本等于 `-Version`）；不依赖 runtime；含 `frameworkReference Microsoft.AspNetCore.App`。
  - 断言 Tool 的 nupkg：包类型为 `DotnetTool`；`tools/net10.0/any/` 下含 5 个 RID 的 `runtimes/<rid>/native/libopenslide-1.*`，以及 SkiaSharp 原生库。
  - 工具冒烟（所有平台，包括 `-SkipPack` 的 unix 运行）：
    1. `dotnet tool install OpenSlideSharp.Tool --tool-path <tmp> --add-source <feed> --version <v>`；
    2. 运行 `openslide-sharp deepzoom <small.svs> -o <tmp>/small`；
    3. 断言 `small.dzi` 存在，且 `small_files` 下至少有一个 `.jpeg`。
- CI：
  - `omezarr-interop` job 额外完成两件事：
    - 用 `dotnet run --project src/OpenSlideSharp.Tool -c Release -- omezarr <boxes.tiff> --chunk 64 --ngff 0.4|0.5` 导出两个 store，由 `verify-omezarr.py --stores` 校验能读、层数与 shape 正确；
    - 后台启动 `samples/OpenSlideSharp.Web`（`dotnet run -c Release -- <boxes.tiff> -p 5055`），轮询 `/zarr/slide.zarr/zarr.json` 直到返回 200（最多 60 秒），然后 `verify-omezarr.py --url http://127.0.0.1:5055/zarr/slide.zarr` 用 zarr + fsspec 读取并校验。
  - `verify-omezarr.py`：
    - 新增 `--stores <dir>...` 与 `--url <url>` 两种模式：读取每层，检查 shape、dtype 和 3 个通道，并用 ome-zarr Reader 解析；
    - 原有的 manifest 模式保持不变；
    - manifest 为空时报错，处理计划 2 遗留的“OK: 0 stores”问题。
- 文档：
  - 根 README 的包表格增加两个包；Usage 增加最小的 ASP.NET Core 嵌入示例（`AddOpenSlide` + `MapDeepZoom` + `MapOmeZarr`）和工具的安装与示例命令；新增一节 “Web sample” 说明运行方式。保持简洁。
  - 各包与示例的 README 写清选项、URL 形式、颜色模式和日志行为。

- [ ] **Step 1: 扩展 `Test-Packages.ps1` 与 PackageSmoke，本机运行**

Run: `pwsh eng/Get-OpenSlide.ps1 -Platform all; pwsh eng/Test-Packages.ps1`
Expected: `All package smoke tests passed.`

- [ ] **Step 2: 扩展 `verify-omezarr.py` 与 CI job，本机按 CI 步骤跑一遍 Python 校验**（venv 放在临时目录）
- [ ] **Step 3: 更新文档**
- [ ] **Step 4: 运行全部测试，提交**

```bash
git add -A && git commit -m "ci: pack and smoke-test the AspNetCore package and tool; verify served OME-Zarr"
```
