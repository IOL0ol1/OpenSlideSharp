# OpenSlideSharp 4.x 重构设计

日期：2026-11-17
状态：草案，待审阅

## 1. 意图

OpenSlideSharp 作为 **公共 .NET 库** 维护，目标是让 .NET 用户获得与 OpenSlide 官方语言绑定同等的工具集，并且自动跟上 OpenSlide 上游：

- 核心绑定始终对齐最新 OpenSlide（当前正式版 4.0.1，nightly 为 `v4.0.1-49-g174630bf`）。
- 提供与 openslide-python 的 `openslide.deepzoom.DeepZoomGenerator` 及其 `examples/deepzoom`（单片 server、多片 multiserver、静态导出 tile）近似的 C# 类库与 ASP.NET Core 实现。
- 提供与 openslide-java 自带的简单图像查看器（`org.openslide.gui.Demo` / `OpenSlideView`）近似的 C# 桌面程序。
- GitHub Actions 自动追踪上游构建，NuGet 包版本号能直接对应到 https://openslide.org/builds/ 上的 OpenSlide 版本与 commit hash。

允许破坏现有 API。包名统一为 `OpenSlideSharp`，`OpenSlideSharp.Interop` 取消。

## 2. 目标 / 非目标 / 反目标

### 目标

1. `OpenSlideSharp` 包：ClangSharp 生成的底层 P/Invoke + 手写的高层 API，覆盖 `openslide.h` 全部公开函数（含 4.x 的 ICC、cache、version、`openslide.barcode` 等）。
2. `OpenSlideSharp.runtime` 包：win-x64 / linux-x64 / linux-arm64 / osx（universal）原生库，引用后在这些平台开箱即用；同一个包也能被 Visual Studio 原生 C++ 项目（vcxproj）直接引用。
3. 零依赖的 `DeepZoomGenerator`，行为与 openslide-python 一致（层级、瓦片划分、overlap、limit_bounds、背景色合成、缩放、ICC 附带）。
4. 零依赖的 `OmeZarrGenerator`（单文件，与 `DeepZoomGenerator` 同在核心包）：把切片动态暴露为 OME-Zarr（NGFF 0.5 / 0.4）。
5. `OpenSlideSharp.SkiaSharp`：瓦片 / 区域 / 关联图像编码为 JPEG、PNG，ICC 转 sRGB。
6. `OpenSlideSharp.AspNetCore`：可嵌入任意 ASP.NET Core 应用的 Deep Zoom 与 OME-Zarr 端点，以及切片缓存。
7. 示例 Web 应用（合并 Python 的 server 与 multiserver）、导出用 dotnet tool `OpenSlideSharp.Tool`（`deepzoom` 子命令对标 `deepzoom_tile.py`，另有 `omezarr` 子命令）、Avalonia 桌面查看器（对标 Java Demo）。
8. CI：每日自动同步上游 nightly 与正式版 → 重新生成绑定 → 四平台测试 → 发布 NuGet 与 GitHub Release。

### 非目标

- `ImageSlide`（用普通图像库打开非 WSI 图片，对应 Python 的 `open_slide()` 回退）。
- 指定渲染意图的颜色模式（Python 的 `absolute-colorimetric` / `perceptual` / `relative-colorimetric` / `saturation`）。Skia 的色彩管理不支持按意图选择；只提供 `Default`（转 sRGB）、`Embed`、`Ignore` 三种。
- 引入 LittleCMS 等需要额外打包原生库的色彩管理库。
- OME-NGFF 0.6（2026-09-14 发布，新的坐标系与变换体系，工具支持尚在跟进）、bioformats2raw 多 series 布局、zstd / blosc 压缩（需原生库）、在 OME-Zarr 中嵌入 ICC。
- C/C++ 方面：CMake / vcpkg 集成（vcpkg 已有 openslide 移植）、Linux / macOS 上经 NuGet 的 C++ 消费、PDB 调试符号（openslide-bin 只提供 DWARF）。
- 发布 openslide-bin 旧系列的维护版（例如 4.0.1.x 之后的 4.0.0.x）。
- 32 位 / x86、Mono / Unity 的专门适配（netstandard 版本能用即可，不做额外保证）。
- Docker 镜像发布。
- 旧 API 的兼容垫片。
- 查看器选区的持久化 / 导出（Java Demo 也没有）。

### 反目标（技术上能用但算失败的情况）

- 在支持的平台上，引用 `OpenSlideSharp.runtime` 后仍需用户手动下载原生库。
- 发布出去的包版本无法追溯到唯一的上游构建。
- 托管包的默认依赖链中出现非宽松许可（例如 ImageSharp 的 Split License）。LGPL 的 runtime 包由用户显式引用，不在默认依赖链里。
- CI 发布未经四平台测试的包。
- 作为公共库的示例只能在 Windows 运行。

## 3. 约束

- OpenSlide 及 openslide-bin 内依赖为 LGPL-2.1 等许可；runtime 包必须附带 openslide-bin 的 `licenses/` 目录，并在包描述中注明源码出处（openslide-bin 源码包）。托管代码保持 MIT。
- openslide-bin 4.x 产物（已核实 4.0.1.2）：
  - Windows：`bin/libopenslide-1.dll`（单文件，依赖静态链接，另有 `.debug`）。
  - Linux：`lib/libopenslide.so.1.0.1`（`libopenslide.so.1` 为符号链接，另有 `.debug`）。
  - macOS：`lib/libopenslide.1.dylib`（arm64+x86_64 universal，另有 `.dSYM`）。
  - 每个归档含 `include/openslide/openslide.h`、`versions.json`、`VERSIONS.md`、`licenses/`。
- 上游版本信息来源：
  - 正式版：openslide/openslide-bin GitHub Releases，tag 如 `v4.0.1.2`；`versions.json` 中 openslide 版本为 `4.0.1`。
  - nightly：openslide/builds GitHub Releases（prerelease），tag 如 `v4.0.1.2+20261102.nightly`；`versions.json` 中 openslide 版本为 git describe 形式 `v4.0.1-49-g174630bf`；builds 页面显示 8 位 hash `174630bf`。
  - **openslide/builds 只保留最近 30 个 nightly**，旧资源会 404。
- nuget.org：版本唯一；最多 4 段数字；`+` 后的元数据不参与唯一性判断；预发布版排序低于同号正式版。
- 目标框架：核心与 SkiaSharp 包 `netstandard2.0;netstandard2.1;net10.0`；AspNetCore 包、工具、示例 `net10.0`（.NET 8 已于 2026-11 结束支持，.NET 10 为当前 LTS）。
- ClangSharpPInvokeGenerator 版本由 `dotnet-tools.json` 固定（当前 21.1.8.2）。

## 4. 方案取舍

| 决策点 | 选项 | 结论 |
|---|---|---|
| 绑定层 | A. ClangSharp 生成的底层保留 C 名 + 手写高层；B. 沿用 SnakeCase→PascalCase 源生成器 | **A**。高层本来就要手写，底层保留 C 名，可与 C 文档一一对应（同 Python 的 `lowlevel`）；去掉一个脆弱环节。 |
| 原生库分发 | A. 单个 `OpenSlideSharp.runtime` 含全部 RID；B. 每个 RID 一个包 | **A**。去掉调试符号后约 10MB，用户只需引用一个包；B 让包数量翻倍且需要用户自己选。 |
| 图像处理 | A. 核心零依赖 + SkiaSharp 适配包；B. ImageSharp；C. System.Drawing / OpenCV | **A**。B 的许可证对年收入超过 100 万美元的公司收费，不适合做 MIT 库的默认依赖；System.Drawing 仅限 Windows；OpenCV 原生依赖过重。 |
| 旧扩展包 | 保留 / 删除 BitmapExtensions、OpencvExtensions、BruTile | **删除**。用户拿像素缓冲区一行代码即可构造 `Mat` / `Bitmap`。 |
| Web | A. 可复用包 `OpenSlideSharp.AspNetCore` + 示例应用；B. 只做示例应用 | **A**。公共库用户需要把瓦片服务嵌进自己的应用。 |
| 桌面查看器 | A. Avalonia + 自写瓦片控件；B. WPF + Mapsui（现有） | **A**。原生库支持三个平台，演示程序也应该能跨平台；Mapsui 带来 BruTile 与地理坐标系转换，Java 版本身也是自绘控件。 |
| 版本号 | 见第 7 节 | 正式版与上游一致；nightly 第四段 +1，预发布标签带日期与 hash。 |

## 5. 仓库结构（重构后）

```
src/
  OpenSlideSharp/                 核心：Native（生成）+ 高层 API + DeepZoom + OME-Zarr
  OpenSlideSharp.runtime/         仅打包原生库（.NET 与 VS C++），无代码
  OpenSlideSharp.SkiaSharp/       编码与色彩转换
  OpenSlideSharp.AspNetCore/      Deep Zoom / OME-Zarr 端点与切片缓存
  OpenSlideSharp.Tool/            dotnet tool `openslide-sharp`：deepzoom / omezarr 导出
samples/
  OpenSlideSharp.Web/             ASP.NET Core 查看器（单片 / 目录两种模式）
  OpenSlideSharp.Viewer/          Avalonia 桌面查看器
test/
  OpenSlideSharp.Tests/           核心 + DeepZoom + Skia
  OpenSlideSharp.AspNetCore.Tests/
  NativeCppSmoke/                 最小 vcxproj，CI 验证 runtime 包的 C++ 消费
eng/
  openslide.json                  当前追踪的上游构建清单（见 7.1）
  GenerateOpenSlide.rsp           ClangSharp 响应文件
  *.ps1                           下载 / 生成 / 计算版本的脚本
.github/workflows/
```

删除：`src/OpenSlideSharp.Interop`、`src/OpenSlideSharp.SourceGenerator`、`src/OpenSlideSharp.BitmapExtensions`、`src/OpenSlideSharp.OpencvExtensions`、`src/OpenSlideSharp.BruTile`、`src/OpenSlideSharp.Windows`、`src/OpenSlideSharp.runtime.win`、`example/` 下三个旧示例、`build/DownloadAndCopy.ps1`、`generation/` 下提交进仓库的 nightly zip 及其解压内容（改为 CI / 脚本按清单下载，且加入 `.gitignore`）。旧的手写 `Interop/NativeMethods.cs` 与自定义 Marshaler 也一并删除。

## 6. 组件设计

### 6.1 `OpenSlideSharp`（核心）

**底层 `OpenSlideSharp.Native`**：ClangSharp 从上游 `openslide.h` 生成，文件提交进仓库（例如 `src/OpenSlideSharp/Native/OpenSlide.Native.g.cs`），保留 C 函数名（`openslide.read_region` 等），属性名常量同样生成。公开但标注为“高级用法”。

**原生库加载**：
- `DllImport` 库名为 `libopenslide-1`。
- runtime 包内统一改名，使 .NET 默认探测无需额外代码即可命中：win-x64 `libopenslide-1.dll`、linux-* `libopenslide-1.so`、osx-arm64 与 osx-x64 均放同一个 universal `libopenslide-1.dylib`。
- `net10.0` 目标额外注册 `NativeLibrary.SetDllImportResolver`：先走默认探测，失败后回退系统库名（`libopenslide.so.1`、`libopenslide.1.dylib`、Homebrew 常见路径），并支持显式指定路径（API 或环境变量，具体形式由实现者定）。
- 移除现有的“改 PATH 环境变量”做法。

**高层 API**（命名对齐 openslide-python，遵循 C# 惯例；以下为草图，细节由实现者定）：

```csharp
public sealed class OpenSlide : IDisposable
{
    public static string? DetectFormat(string path);        // openslide_detect_vendor
    public static string LibraryVersion { get; }             // openslide_get_version
    public static OpenSlide Open(string path);               // 失败抛 OpenSlideException
    public int LevelCount { get; }
    public SlideSize Dimensions { get; }
    public IReadOnlyList<SlideSize> LevelDimensions { get; }
    public IReadOnlyList<double> LevelDownsamples { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }
    public IReadOnlyDictionary<string, AssociatedImage> AssociatedImages { get; } // 延迟读取
    public byte[]? ColorProfile { get; }                     // ICC
    public int GetBestLevelForDownsample(double downsample);
    public void ReadRegion(long x, long y, int level, int w, int h, Span<byte> dest); // 预乘 BGRA，零拷贝
    public PixelImage ReadRegion(long x, long y, int level, int w, int h);
    public PixelImage GetThumbnail(int maxWidth, int maxHeight);
    public void SetCache(OpenSlideCache cache);
}
public sealed class OpenSlideCache : IDisposable { public OpenSlideCache(long capacityBytes); }
public sealed class PixelImage { int Width; int Height; PixelFormat Format; byte[] Data; byte[]? IccProfile; }
```

- 句柄用 `SafeHandle` 包装，保证 `Dispose` 不会与正在执行的读取竞争。OpenSlide 除 `close` 外都线程安全。
- 每次调用后检查 `openslide_get_error`，进入错误状态即抛 `OpenSlideException`，与 Python 的行为一致。
- 字符串统一按 UTF-8 编解码（Windows 上的文件名同样如此）。
- `PixelFormat` 至少包含：`Bgra32Premultiplied`（OpenSlide 原始格式）、`Bgra32`（非预乘）、不透明 BGRA（DeepZoom 输出，alpha 全为 255）。核心提供它们之间的转换。

**`DeepZoomGenerator`**：逐行移植 openslide-python `deepzoom.py`（main 分支，参见 https://github.com/openslide/openslide-python/blob/main/openslide/deepzoom.py）：
- 构造参数与默认值相同：`tileSize = 254`、`overlap = 1`、`limitBounds = false`。
- 四个坐标平面（t / z / l / l0）的计算，以及 `limit_bounds` 时按 bounds 属性缩放各层尺寸。
- 对外成员：`LevelCount`、`LevelTiles`、`LevelDimensions`、`TileCount`、`GetDzi(format)`、`GetTile(level, col, row)`、`GetTileCoordinates`、`GetTileDimensions`。
- `GetTile` 的处理顺序：
  1. 读取区域；
  2. 合成到 `openslide.background-color`（缺省 `ffffff`）；
  3. 若尺寸不等于 z_size，则高质量缩小到 z_size（Lanczos-3 或同等质量，不要求与 Pillow 像素级一致，但尺寸必须一致）；
  4. 把切片 ICC 附在结果上。
- 层级、瓦片数、坐标、尺寸、DZI 文本必须与 Python 完全一致，以移植 openslide-python 的 `tests/test_deepzoom.py` 断言值来保证。
- 现有 C# 实现与 Python 的差异（不做背景合成和缩放、`limitBounds` 默认 true、层级计算方式不同）全部按 Python 改正。

**共享渲染流程**：把“选最佳层级 → `ReadRegion` → 合成背景色 → 缩放到目标尺寸”抽成内部组件，`DeepZoomGenerator` 与 `OmeZarrGenerator` 共用。核心定义颜色转换接口（例如 `IColorTransform`，作用于不透明 BGRA 像素），两个生成器都可选地接受它；默认实现由 `OpenSlideSharp.SkiaSharp` 提供（见 6.3）。

**`OmeZarrGenerator`**（单文件，与 `DeepZoomGenerator` 同目录）：

- 规范版本：`NgffVersion.V0_5`（Zarr v3，默认）与 `V0_4`（Zarr v2）。
- 金字塔：第 0 层为全分辨率，之后每层宽高减半（向上取整），直到整幅图能放进单个 chunk 为止。每层像素通过共享渲染流程生成，与 Deep Zoom 的处理方式一致。
- 数组：轴为 `c, y, x`（`c` 类型为 channel，`y`、`x` 类型为 space），数据类型 `uint8`，3 个通道（RGB，背景色已合成，不带 alpha）。默认 chunk 为 `1×512×512`，可配置。
- 边缘 chunk 补齐到完整 chunk 尺寸，补齐区域填 0（即 fill_value）。
- 压缩：`None` 或 `Gzip`（级别可配），使用 BCL 的 `System.IO.Compression`。
  - v3 codecs：`bytes`，可选再加 `gzip`。
  - v2 compressor：`gzip` 或 null；`dimension_separator` 为 `/`。
- 元数据由手写 JSON 生成，不引入 JSON 库，保持零依赖。字符串需正确转义。
  - `multiscales`：axes 带单位；每层的 `coordinateTransformations` 含 `scale`。有 `openslide.mpp-x/y` 时单位为 micrometer，scale = mpp × 2^层号；没有时不写单位，scale 为 2^层号。开启 `limitBounds` 时追加 `translation`，取 bounds 偏移换算后的物理坐标。
  - `omero`：渲染元数据，三个通道分别为红、绿、蓝，window 为 0–255，便于查看器直接按 RGB 显示。
- 对外 API（草图）：
  - `GetGroupMetadata()`：v3 返回根 `zarr.json`；v2 返回 `.zgroup` 与 `.zattrs`。
  - `GetArrayMetadata(level)`。
  - `GetChunk(level, c, y, x)`：返回已编码的字节。
  - `TryGetEntry(key)`：把任意 Zarr key 解析为内容；不存在返回 false。服务端和导出都只需调用这一个方法。
- 关联图像：每张作为一个独立的单层 OME-Zarr，用同一个类以 `AssociatedImage` 作为源构造。
- 颜色：模式只有 `Default`（经 `IColorTransform` 转为 sRGB）和 `Ignore` 两种。没有提供转换器、但切片带 ICC 时，按 `Ignore` 处理并记录一次警告。
- 同一 (level, y, x) 区域的三个通道 chunk 共用一次渲染：由生成器内部或服务端的小型 LRU 缓存保证，具体放在哪一层由实现者决定。

### 6.2 `OpenSlideSharp.runtime`

- **依赖关系**：`OpenSlideSharp`、`OpenSlideSharp.SkiaSharp`、`OpenSlideSharp.AspNetCore` 都 **不** 依赖本包，对应 Python 中 openslide-python 与 openslide-bin 的关系；想用系统 libopenslide 的用户可以不装。README 快速上手同时引用 `OpenSlideSharp` 与 `OpenSlideSharp.runtime`，并建议两者保持同一版本。`DeepZoomTile` 工具和两个示例直接引用本包。net10.0 上的系统库回退因此是正式支持的路径，不只是兜底。
- 无托管代码。`runtimes/{win-x64,linux-x64,linux-arm64,osx-arm64,osx-x64}/native/` 按 6.1 的命名放置，不含 `.debug` / `.dSYM`，不含 `slidetool`。
- 通过 `buildTransitive` targets 支持 .NET Framework（SDK 风格项目）复制 win-x64 DLL。.NET 的 targets 放在带 TFM 的目录下（如 `buildTransitive/net462/`），避免被 C++ 项目导入。
- **VS C++ 支持**：包内增加 `build/native/`，内容如下：
  - `include/openslide/openslide.h`、`openslide-features.h`；
  - `lib/x64/libopenslide.lib`（openslide-bin 自带的 MSVC 导入库）；
  - `bin/x64/libopenslide-1.dll`，或直接引用 `runtimes/win-x64/native/` 下的同一文件，避免包内重复；
  - `OpenSlideSharp.runtime.targets`：仅在 `$(Platform)` 为 `x64` 时添加 `AdditionalIncludeDirectories`、`AdditionalLibraryDirectories`、`AdditionalDependencies`，并在构建后把 DLL 复制到 `$(OutDir)`。`Win32` / `ARM64` 平台直接报出明确的构建错误。
  - 支持 `packages.config`（C++ 项目的默认方式）以及 VS 2026 18.7+ 的原生 `PackageReference`（`EnableNativePackageReferenceSupport`）。
  - nuspec 的 tags 含 `native`。
  - Windows 的 DLL 文件名保持 `libopenslide-1.dll` 不变，因为导入库内部引用的就是这个名字。
- 附带 `licenses/`、`VERSIONS.md`、`versions.json`。
- 包许可声明为 LGPL-2.1-only 并附许可证文件，具体写法由实现者定。

### 6.3 `OpenSlideSharp.SkiaSharp`

- 依赖 SkiaSharp 3.x，以及 `SkiaSharp.NativeAssets.Linux.NoDependencies`，保证 Linux 容器开箱即用。
- 提供 `PixelImage` → `SKBitmap` / `SKImage`、编码为 JPEG（质量参数）/ PNG 的扩展方法。
- `ColorMode` 三种模式：
  - `Default`：用切片 ICC 经 Skia 转为 sRGB，输出不带 ICC。
  - `Embed`：原样输出，并把 ICC 嵌入编码结果。
  - `Ignore`：丢弃 ICC。
  - 无 ICC 时三者行为相同。
  - 与 Python `deepzoom_server.py` 的 `get_transform` 语义对齐，渲染意图除外（见非目标）。
- `Default` 的实现分三级。原因是 `SKColorSpace.CreateIcc` 只接受“矩阵 + 参数曲线”类 profile，对只有 A2B/LUT、采样 TRC、三通道曲线不一致的 profile 都返回 null，而扫描仪的 profile 常属于这几类。
  1. `SKColorSpace.CreateIcc` 成功：直接用 Skia 转为 sRGB。
  2. 失败：在内存中把瓦片编码为不压缩的 PNG 并写入 iCCP，再用 `SKCodec` 以 sRGB 为目标解码。这条路径走 skcms，支持 A2B/LUT profile。实现者需验证这一点，并对每个切片缓存“走哪一级”的判断结果。
  3. skcms 也无法解析：降级为 `Embed`（浏览器会按嵌入的 ICC 做色彩管理），每个切片只记录一次警告。
  - 不引入 LittleCMS：它要为四个平台打包原生库，还有额外的许可工作，列为非目标。
  - 测试素材需要两类 ICC：一个矩阵/TRC 类、一个 A2B/LUT 类。由实现者找许可宽松的公开 profile，或自行合成。
- 上述 sRGB 转换同时以核心定义的 `IColorTransform` 接口暴露，供 `OmeZarrGenerator` 和 `DeepZoomGenerator` 在渲染阶段使用。第 3 级的降级在 OME-Zarr 中表现为不做转换（`Ignore`）并记录警告，因为 OME-Zarr 无法嵌入 ICC。
- 若 Skia 的 JPEG / PNG 编码器无法嵌入 ICC，`Embed` 模式由实现者补写对应的元数据段（JPEG APP2 / PNG iCCP）。

### 6.4 `OpenSlideSharp.AspNetCore`（net10.0）

- 服务注册：`AddOpenSlideDeepZoom(options)`。选项与 Python 命令行参数对应：`TileSize`、`Overlap`、`Format`（jpeg / png）、`Quality`、`LimitBounds`（Python 的 `--ignore-bounds` 取反）、`ColorMode`、`SlideCacheSize`（Python multiserver 默认 10）。
- 端点映射：`MapDeepZoom(prefix, resolver)`，提供 `{slide}.dzi` 与 `{slide}_files/{level}/{col}_{row}.{format}`，以及关联图像各自的 DZI 和瓦片端点。
- `resolver` 把 URL 中的路径映射到文件：内置“单文件”和“根目录”两种。目录模式必须防止路径穿越（只允许解析到根目录内部）。
- 切片缓存：LRU，键为文件路径。淘汰时安全释放，正在被请求使用的实例延迟到请求结束后再释放；多个请求同时打开同一切片时只打开一次。
- OME-Zarr 端点：`MapOmeZarr(prefix, resolver)`，与 Deep Zoom 共用切片缓存与 resolver。
  - 把 `{slide}.zarr/{key}` 交给 `OmeZarrGenerator.TryGetEntry`，key 不存在返回 404。`.zmetadata` 等探测请求也应正确返回 404。
  - 支持 `HEAD` 请求。
  - 关联图像映射到 `{slide}/associated/{name}.zarr/...`，具体路径形式由实现者定。
  - 提供 CORS 选项（默认允许任意来源的 GET / HEAD），因为 vizarr 等 Web 查看器会跨域读取。
- 不包含页面和视图，页面在示例应用里。

### 6.5 `samples/OpenSlideSharp.Web`

- 合并 Python 的 `deepzoom_server.py` 与 `deepzoom_multiserver.py`：配置项 `SlidePath` 指向文件时为单片模式，指向目录时为目录浏览模式（递归列出可识别的切片）。
- 页面对标 Python 模板：
  - OpenSeadragon 查看器，配比例尺插件（根据 `openslide.mpp-x` 显示比例尺）；
  - 属性表；
  - 关联图像列表，点击后在同一查看器中切换。
- 前端静态资源（OpenSeadragon、scalebar 插件、jQuery 若需要）随应用放在 `wwwroot`，不走 CDN，并附带它们的许可证（参照 Python 示例的 `licenses/`）。
- 切片页面显示该切片的 OME-Zarr 地址，并提供一个可选的“在 vizarr 中打开”外链（指向公开托管的 vizarr，`?source=<地址>`）。这是唯一的外部依赖，离线环境下不可用不影响其他功能。
- 配置走 `appsettings.json` 与命令行参数，参数名对应 Python 的选项。

### 6.6 `OpenSlideSharp.Tool`（dotnet tool，命令 `openslide-sharp`）

- `openslide-sharp deepzoom`：对标 `deepzoom_tile.py`。
  - 参数：切片路径、`-o` 输出名、`-s` 瓦片尺寸、`-e` overlap、`-f` 格式、`-Q` 质量、`-B` 忽略 bounds、`-c` 颜色模式、`-j` 并发数、`-r` 生成带关联图像的查看器 HTML。
  - 输出 `<name>.dzi` 与 `<name>_files/`。
- `openslide-sharp omezarr`：导出 `<name>.zarr/` 目录。
  - 参数：`--ngff 0.5|0.4`、`--chunk`、`--compression none|gzip`、`--gzip-level`、`-B`、`-c`、`-j`，以及 `--associated`（是否同时导出关联图像）。
  - 实现方式：遍历 `OmeZarrGenerator` 的全部 key 并写入文件。
- 两个子命令都用多线程并行（OpenSlide 线程安全，无需像 Python 那样用多进程）。

### 6.7 `samples/OpenSlideSharp.Viewer`（Avalonia）

功能对标 Java `Demo` / `OpenSlideView`：
- 打开文件：命令行参数或文件对话框。
- 主视图：拖动平移，滚轮以光标为中心缩放；按当前缩放取最佳层级，后台线程读取瓦片，内存中做 LRU 缓存；先显示低分辨率再替换为高分辨率。
- 缩略图导航：显示当前视口矩形，可点击或拖动跳转。
- 属性表、关联图像列表（点击后打开窗口查看）。
- 矩形选区：在主视图拖出选区，选区列表可“缩放到选区”。
- 状态栏显示坐标与缩放倍率，有 MPP 时同时显示物理尺寸。

## 7. CI/CD 与版本

### 7.1 上游清单 `eng/openslide.json`

记录当前追踪的上游构建，例如：

```json
{
  "channel": "nightly",
  "binVersion": "4.0.1.2+20261102.nightly",
  "openslideVersion": "v4.0.1-49-g174630bf",
  "openslideCommit": "174630bf",
  "date": "2026-11-02",
  "assets": { "win-x64": { "url": "...", "sha256": "..." }, "linux-x64": {}, "linux-arm64": {}, "osx": {} }
}
```

本地脚本和 CI 都按清单下载并校验 sha256。资源 404 时（nightly 已被上游清理），脚本要给出清楚的提示，并提供“改用最新构建”的选项。

### 7.2 版本号规则

| 上游 | 示例 | NuGet 版本 |
|---|---|---|
| 正式版 openslide-bin `X.Y.Z.B` | `4.0.1.2` | `4.0.1.2` |
| nightly `X.Y.Z.B+YYYYMMDD.nightly`，openslide `…-g<hash>` | `4.0.1.2+20261102.nightly` / `g174630bf` | `4.0.1.3-nightly.20261102.g174630bf` |
| 上游未变、只改了 C# 代码（第 N 次） | 同上 | `4.0.1.3-nightly.20261102.g174630bf.N` |

- nightly 的第四段 +1，是为了让 nightly 排在已发布的正式版之后，否则 `--prerelease` 永远拿不到 nightly。
- 正式版的 openslide commit hash 写进包的 release notes 与程序集元数据（`AssemblyInformationalVersion`，如 `4.0.1.2+g<hash>`）。nightly 同样写入完整的上游版本串。
- 当前清单若是正式版，又有只改 C# 代码的发布，则按 nightly 规则编号：日期取该正式版的发布日期，hash 取该 tag 的 commit。
- 所有包（`OpenSlideSharp`、`.runtime`、`.SkiaSharp`、`.AspNetCore`、`.Tool`）共用同一个版本号。
- 已知代价：只改 C# 代码无法单独发正式版，要等上游下一个正式版。

**同步与发布规则**（nuget.org 上的版本只能 unlist、不能删，排序错乱的版本收不回来，所以规则要严）：

1. **单调不变式**：发布流程在推送前查询 nuget.org 上 `OpenSlideSharp` 已有的最高版本（含预发布版）。计算出的版本不高于它就拒绝发布。所有规则都以这一条兜底。
2. **正式版**：只接受版本号高于清单当前版本、也高于已发布最高版本的 openslide-bin 正式版。旧系列的维护版直接跳过，不发布、不改清单，列为非目标。例如 4.0.1.1 之后才出现的 4.0.0.14。
3. **nightly**：按 7.2 算出的版本必须高于已发布最高版本，否则跳过。例如正式版 4.0.1.3 发布后，基线仍是 4.0.1.2 的 nightly 会算出 `4.0.1.3-nightly…`，低于 4.0.1.3，因此跳过，等基线跟上再发。
4. **处理顺序**：一次同步发现多个候选时，按 NuGet 版本从低到高依次处理。清单最终停在最高的那个，**清单永不降级**。
5. **API 范围**：手写高层只封装最新 OpenSlide **正式版** 头文件中已有的 API。nightly 才有的新 API 只出现在生成的 `Native` 层里。清单从 nightly 切到正式版时，若这些 API 随头文件消失，属于预期行为：预发布 API 本来就不稳定，release notes 里注明。

### 7.3 Workflows

1. **`ci.yml`**：在 push 和 PR 时运行。按清单下载原生库，在四个平台上构建并测试：windows-latest、ubuntu-latest、ubuntu-24.04-arm、macos-latest。不发布。
2. **`upstream-sync.yml`**：每日定时，也可手动触发。
   - 查询 openslide/builds 最新 nightly 和 openslide-bin 最新正式版，与清单比较。
   - 有更新时：更新清单，用 ClangSharp 重新生成底层绑定，跑完整测试矩阵，通过后由机器人提交到 master，再触发发布。
   - 同一天既有正式版又有 nightly 时，先发正式版。
   - 生成后手写层编译失败或测试失败：不提交、不发布，并自动开 issue（同一上游版本不重复开）。
   - master 受分支保护无法直接推送时，改为开 PR。
3. **`release.yml`**：由同步提交触发，或在 `src/**` 有变更时由 push 触发。
   - 按 7.2 计算版本号，用已有的 git tag 决定 N。
   - 打包并推送 nuget.org（`--skip-duplicate`）。
   - 打 tag `v<version>`，创建 GitHub Release：附 nupkg，以及桌面查看器的自包含单文件构建（win-x64、linux-x64、osx-arm64）。nightly 标为 prerelease。
- 推送凭据：沿用现有的 `NUGET_API_KEY` secret；改用 nuget.org Trusted Publishing（OIDC）由实现者视情况决定。
- ClangSharp 生成步骤沿用现在在 windows-latest 上验证过的方式；头文件各平台相同。

## 8. 测试

- 核心测试：移植 openslide-python 的 `tests/test_base.py`、`test_slide.py` 中适用的部分，以及 `test_deepzoom.py` 的断言值。使用仓库现有资源 `boxes.tiff`、`small.svs`、`unopenable.tiff`、`unreadable.svs`；Python 测试用到而仓库缺少的资源，从 openslide-python 测试目录补齐。
- DeepZoom 一致性：层级、瓦片数、各瓦片坐标与尺寸、DZI 文本与 Python 的断言完全一致。
- Skia：三种颜色模式的输出（是否带 ICC、尺寸、格式）。
- AspNetCore：用 `WebApplicationFactory` 覆盖 DZI 与瓦片端点、越界瓦片返回 404、路径穿越被拒绝、缓存淘汰时不影响进行中的请求。
- runtime 包冒烟测试：CI 在四个平台上用打好的 nupkg 新建一个控制台项目，调用 `OpenSlide.LibraryVersion` 并打开 `small.svs`，验证打包和库名探测都正确。
- C++ 冒烟测试：windows-latest 上用 MSBuild 编译 `test/NativeCppSmoke`（packages.config，从本地源还原 runtime 包，x64），运行后打印 `openslide_get_version()` 并打开 `small.svs`。
- OME-Zarr：
  - 用官方 NGFF JSON Schema（0.4 与 0.5，放进测试资源）校验生成的元数据。
  - chunk 像素与相同区域的 `ReadRegion` 经共享渲染流程处理后的结果逐字节一致，边缘补齐正确。
  - 互操作：CI 安装 Python 的 `zarr` 与 `ome-zarr`，读取工具导出的 0.4 和 0.5 目录（也包括 AspNetCore 端点提供的 URL），与 C# 侧的像素对比。

## 9. 迁移与发布事项

- README 重写：包列表、快速上手、版本号规则说明、各示例的运行方法。
- 旧包 `OpenSlideSharp.Interop`、`OpenSlideSharp.Windows`、`OpenSlideSharp.runtime.win`、`OpenSlideSharp.BitmapExtensions`、`OpenSlideSharp.OpenCVExtensions`、`OpenSlideSharp.BruTile` 需要在 nuget.org 上标记弃用并指向新包，由维护者手动操作，README 列出清单。
- `OpenSlideSharp` 包版本从 1.1.2 跳到 4.0.1.x，属于预期的大版本变化。

## 10. 设计者做出的判断（维护者未逐条确认，供审阅）

1. 原生库在 runtime 包内统一改名为 `libopenslide-1.*`，让 netstandard 目标不依赖解析器也能加载。
2. 颜色模式只有 `Default` / `Embed` / `Ignore`；`Default` 走“Skia 直接转换 → SKCodec 解码 → 降级为 Embed”三级。
3. `DeepZoomGenerator` 的 `limitBounds` 默认值改为与 Python 一致的 `false`（现有 C# 为 `true`），属行为变化。
4. 核心、SkiaSharp、AspNetCore 包都不依赖 runtime 包。
5. 同步规则：单调不变式、跳过旧系列维护版、跳过低于已发布版本的 nightly、清单永不降级、手写层只用正式版 API。
6. 当前清单为正式版时，只改 C# 的发布按 nightly 规则编号，日期和 hash 取该正式版的。
7. Python 测试用到而仓库缺少的切片素材，从 openslide-python 补齐。
8. OME-Zarr 的几项选择：默认 NGFF 0.5、可选 0.4；每层缩小 2 倍；轴为 `c,y,x`，chunk 为 `1×512×512`；压缩为不压缩或 gzip；关联图像各自作为独立的单层 OME-Zarr。
9. 导出工具由 `DeepZoomTile` 改为 `OpenSlideSharp.Tool`，命令 `openslide-sharp`，下设两个子命令。
10. runtime 包的 C++ 支持只覆盖 Windows x64。

## 11. 留给实现者决定

- 高层 API 的具体成员签名、类型命名（`SlideSize`、`PixelImage` 等仅为草图）。
- 缩放算法的具体实现，以及缓冲区池化等性能细节。
- 原生库路径覆盖 API 的具体形式。
- 查看器的界面布局与样式、瓦片缓存大小。
- workflow 的具体拆分与复用方式（composite action / reusable workflow）。
- 测试的组织方式。
