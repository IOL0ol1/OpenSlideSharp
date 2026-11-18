# Plan 4：Avalonia 桌面查看器 Implementation Plan

**Goal:** 交付 `samples/OpenSlideSharp.Viewer`，一个跨平台的 Avalonia 桌面查看器，功能对标 openslide-java 的 `Demo` / `OpenSlideView`：
- 打开切片；
- 拖动平移，滚轮以光标为中心缩放；
- 按缩放选层级，后台读取瓦片，LRU 缓存，先显示低分辨率再替换；
- 缩略图导航；
- 属性表；
- 关联图像窗口；
- 矩形选区及“缩放到选区”；
- 状态栏显示坐标、缩放倍率和物理尺寸。

**Architecture:**
- 查看器分成两层：
  - 与 UI 无关的 `Core/`：视口数学、瓦片网格、按字节计的 LRU 缓存、带代次取消的后台加载器、选区模型。可以用纯 xUnit 测试。
  - Avalonia 层：自绘控件 `SlideView`、`MainWindow`、导航器、关联图像窗口。用 `Avalonia.Headless` 做渲染冒烟测试。
- 瓦片像素直接来自 `OpenSlide.ReadRegion` 的预乘 BGRA，与 Avalonia 的 `Bgra8888 / Premul` 一致，不需要转换。
- 另提供一个不开窗口的 `--check <slide>` 模式，供 CI 和计划 5 验证自包含发布包里的原生库。

**Tech Stack:**
- .NET 10
- Avalonia 12.1.6：`Avalonia`、`Avalonia.Desktop`、`Avalonia.Themes.Fluent`、`Avalonia.Fonts.Inter`、`Avalonia.Headless`。Avalonia 依赖 SkiaSharp 3.119.4，与仓库固定的版本一致。
- xUnit v3

**Spec:** `docs/design/2026-11-17-openslidesharp-v4-refactor-design.md`（§6.7）；参考 openslide-java 的 `org/openslide/gui/Demo.java`、`OpenSlideView.java`（main 分支）。遗留事项见 `docs/notes/2026-11-18-plan3-carryover.md`。

## Global Constraints

- **项目与依赖：**
  - `samples/OpenSlideSharp.Viewer`：`net10.0`，`OutputType=WinExe`，`IsPackable=false`。
  - 只依赖核心包 `OpenSlideSharp`（ProjectReference）和 Avalonia，**不**依赖 AspNetCore 和 `OpenSlideSharp.SkiaSharp`。
  - 原生库：`UseOpenSlideRuntimePackage == 'true'` 时用 `PackageReference OpenSlideSharp.runtime`（版本为 `$(Version)`），否则导入 `eng/OpenSlideNative.targets`。做法与工具相同。
  - Avalonia 各包版本统一为 12.1.6，写入 `Directory.Packages.props`。**不**使用 `Avalonia.Headless.XUnit`：它依赖 xunit.v3 3.x，与仓库的 4.0.3 不一致。测试直接用 `Avalonia.Headless` 的会话 API。
- **交互**（与 Java Demo 一致，缩放步长改为连续的 2^(1/4)）：
  - 左键拖动平移；
  - 滚轮以光标为中心缩放，每格缩放 2^(1/4)；
  - Shift + 左键拖动画矩形选区；
  - 方向键或 W/A/S/D 平移 100 像素；
  - `+`/`=` 放大、`-` 缩小，均以视图中心为准；
  - `Z` 适配窗口，`1` 缩放到 1:1（downsample 为 1），空格居中。
- **缩放范围：** downsample 取值在 `[1/4, max(levelDownsamples[^1], 适配窗口的 downsample) × 2]`。即最多放大到 4 倍像素，最小缩到整图适配窗口再缩一半。
- **渲染：**
  - 层级：`slide.GetBestLevelForDownsample(downsample)`。
  - 瓦片在该层级坐标下为 256×256（边缘瓦片按实际尺寸）。
  - 目标层级的瓦片尚未加载时，退回到已缓存的更粗层级瓦片，把覆盖区域缩放绘制，实现“先低分辨率再高分辨率”。
  - 背景色取 `openslide.background-color`（缺省为白色）。
  - 颜色：不做 ICC 转换，与 Java Demo 一致。README 中说明这一点。
- **加载：**
  - 后台工作线程数为 `min(4, ProcessorCount)`。
  - 视口变化时代次（generation）加 1，旧代次中尚未开始的请求直接丢弃，已经开始的照常完成并写入缓存。
  - 同一瓦片不重复加载。
  - 读取失败（如 `OpenSlideException`）时，在状态栏显示一次错误，之后不再为该切片发起请求。
- **缓存：** LRU，上限默认 256 MiB（按 `width × height × 4` 计），线程安全。切换切片或关闭窗口时清空。
- **状态栏：**
  - 光标处 level-0 坐标：`x, y`，光标不在图上时为空；
  - 缩放：`downsample` 保留两位小数；有 `openslide.objective-power` 时同时显示有效倍率 `objective / downsample`，例如 `10.0×`；
  - 有 mpp 时，显示视口宽高和当前选区的物理尺寸（µm 或 mm，自动选单位）。
- **打开方式：**
  - 命令行第一个参数；
  - File → Open（`StorageProvider.OpenFilePickerAsync`）；
  - 拖放文件到窗口。
- **错误处理：** 打不开时显示对话框，或在状态栏提示（含文件名与原因），程序不退出。
- **已知问题：** Windows 上损坏的瓦片会直接终止进程（CFG/longjmp，见 known-issues），README 中写明。
- **`--check <slide>` 模式：**
  - 不创建窗口；
  - 打开切片，读取最低分辨率层级左上角 256×256 区域；
  - 输出 `openslide=<OpenSlideLibrary.Version>` 和 `slide=<w>x<h>`，成功时退出码 0；
  - 打不开时退出码为 1；缺少原生库时退出码为 3，并输出 `DllNotFoundException` 的消息。
- **提交规范：**
  - 提交身份 `IOL0ol1 <1353839731@qq.com>`（local config 已设置）。
  - 引用 Skia 类型时用文件顶部的 `using SkiaSharp;`，不写 `SkiaSharp.X` 这样的限定名（R11）。

## Review Focus

1. **快速连续缩放或平移（每秒几十次视口变化）。** 旧视口的请求不得堆积；内存受缓存上限约束；UI 线程不阻塞在 `ReadRegion` 上。→ Task 1 `Loader_NewGeneration_DropsQueuedRequests`、`Cache_StaysWithinBudget`；Task 2 `SlideView_RenderDoesNotReadOnUiThread`。
2. **在加载过程中关闭窗口或切换切片。** 后台线程不得访问已释放的 `OpenSlide`（不得出现未处理的 `ObjectDisposedException`），切换后不得显示旧切片的瓦片。→ Task 1 `Loader_DisposeWhileLoading_CompletesQuietly`；Task 3 `SwitchSlide_DoesNotShowOldTiles`。
3. **极端尺寸的切片。** 包括 16×16 的小图、只有 1 层的大图，以及缺少 mpp 或 objective-power 属性的切片。适配窗口、1:1 和导航器都不出现除零、NaN 或负尺寸。→ Task 1 `Viewport_TinySlide_FitAndClamp`、`Viewport_NoLevels_Defaults`；Task 3 `StatusBar_NoMpp_OmitsPhysicalSize`。
4. **打不开或损坏的文件**（`unopenable.tiff`、文本文件）**不能让程序崩溃**。→ Task 3 `Open_UnopenableFile_ShowsErrorAndKeepsRunning`。
5. **高 DPI**（缩放 150%/200%）。瓦片按物理像素清晰绘制，坐标换算使用逻辑像素，选区和导航矩形位置正确。→ Task 2 `SlideView_RenderScaling_UsesPhysicalPixels`（headless 模式下设 RenderScaling 为 2）。

---

### Task 1: 项目骨架与 UI 无关的核心（视口、网格、缓存、加载器、选区）

**Files:**
- Create:
  - `samples/OpenSlideSharp.Viewer/OpenSlideSharp.Viewer.csproj`、`Program.cs`（含 `--check`）、`App.axaml(.cs)`（最小 Fluent 主题与空 MainWindow 占位）
  - `samples/OpenSlideSharp.Viewer/Core/Viewport.cs`、`TileGrid.cs`、`TileKey.cs`、`TileCache.cs`、`TileLoader.cs`、`Selection.cs`、`SelectionList.cs`
  - `test/OpenSlideSharp.Viewer.Tests/OpenSlideSharp.Viewer.Tests.csproj`：xUnit v3 MTP，ProjectReference 指向查看器，fixtures 从 `../OpenSlideSharp.Tests/Fixtures/**` 链接，导入 `eng/OpenSlideNative.targets`
  - `test/OpenSlideSharp.Viewer.Tests/ViewportTests.cs`、`TileGridTests.cs`、`TileCacheTests.cs`、`TileLoaderTests.cs`、`CheckModeTests.cs`
- Modify: `OpenSlideSharp.slnx`、`Directory.Packages.props`（Avalonia 12.1.6 各包）

**Interfaces（`internal`，命名空间 `OpenSlideSharp.Viewer.Core`；查看器项目对测试项目开放 InternalsVisibleTo）：**

```csharp
readonly record struct TileKey(int Level, long Column, long Row);

sealed class Viewport
{
    Viewport(SlideSize slideSize, IReadOnlyList<double> levelDownsamples);
    double Downsample { get; }                 // 屏幕上一个逻辑像素对应的 level-0 像素数
    double CenterX { get; } double CenterY { get; }   // level-0 坐标
    double ViewWidth { get; } double ViewHeight { get; } // 逻辑像素
    void Resize(double width, double height);
    void ZoomAt(double screenX, double screenY, double factor); // factor > 1 放大；光标下的 level-0 点保持不动
    void PanBy(double dxScreen, double dyScreen);
    void Fit(); void OneToOne(); void Center();
    void CenterOn(double x0, double y0);
    void ZoomTo(double x0, double y0, double width0, double height0); // 适配一个 level-0 矩形（选区）
    (double X, double Y) ScreenToSlide(double sx, double sy);
    (double X, double Y) SlideToScreen(double x0, double y0);
    (double MinDownsample, double MaxDownsample) Limits { get; }
    event Action? Changed;
}

sealed class TileGrid
{
    const int TileSize = 256;
    TileGrid(IReadOnlyList<SlideSize> levelDimensions, IReadOnlyList<double> levelDownsamples);
    IEnumerable<TileKey> VisibleTiles(int level, double left0, double top0, double right0, double bottom0); // level-0 视窗矩形
    (long X0, long Y0, int Width, int Height) Region(TileKey key); // ReadRegion 参数：level-0 左上角 + 该层宽高
    (double Left0, double Top0, double Right0, double Bottom0) Bounds(TileKey key);
}

sealed class TileCache : IDisposable
{
    TileCache(long budgetBytes);
    bool TryGet(TileKey key, out TileImage? tile);
    void Add(TileKey key, TileImage tile);   // 超出预算时淘汰最久未用者（Dispose 被淘汰的 TileImage）
    long SizeBytes { get; } int Count { get; }
    void Clear();
}

sealed class TileImage : IDisposable           // 平台无关的像素容器：预乘 BGRA
{
    int Width { get; } int Height { get; } byte[] Pixels { get; }  // UI 层在首次绘制时转换为 Avalonia Bitmap 并缓存
}

sealed class TileLoader : IDisposable
{
    TileLoader(Func<TileKey, TileImage> read, TileCache cache, int workers, Action<TileKey> loaded);
    long Generation { get; }
    void Request(IEnumerable<TileKey> keys);   // 新代次：替换队列，按给定顺序（中心优先）处理；已缓存或已在途的跳过
    event Action<Exception>? Failed;           // 首次失败后停止接受新请求
}

sealed record Selection(double X0, double Y0, double Width0, double Height0); // level-0 矩形
sealed class SelectionList { IReadOnlyList<Selection> Items { get; } void Add(Selection s); void RemoveAt(int i); event Action? Changed; }
```

- **视口：** 初始状态为适配窗口并居中。`ZoomAt` 与 `Fit` 都会把 downsample 限制在 `Limits` 之内。视口尺寸为 0 时，`Fit` 保持原状态。
- **加载器：**
  - `Request` 先清空尚未开始的旧请求，再按调用方给出的顺序入队；
  - 工作线程从队列取出瓦片，调用 `read` 读取，写入缓存，再触发 `loaded`（在工作线程上触发，UI 层自己切回 UI 线程）；
  - `Dispose` 先停止接收请求，再等待正在执行的读取结束，最多等 5 秒；之后不再触发任何回调。
- **`--check` 模式：** 写在 `Program.Main` 中，不启动 Avalonia。

- [ ] **Step 1: 写失败的测试**

`ViewportTests`：
- `Viewport_Fit_CentersWholeSlide`：slide 为 300×250，视口为 150×125。`Downsample == 2`，中心为 (150, 125)。
- `Viewport_ZoomAt_KeepsPointUnderCursor`：在屏幕坐标 (30, 40) 处缩放 1.5 倍前后，`ScreenToSlide(30, 40)` 的结果误差不超过 1e-9。
- `Viewport_ZoomClampsToLimits`：缩放 1e6 倍后 downsample 等于 MinDownsample（0.25）。
- `Viewport_OneToOne_SetsDownsample1`。
- `Viewport_TinySlide_FitAndClamp`：slide 为 16×16，视口为 800×600。Fit 后 downsample 等于 0.25（下限）。所有输出都不含 NaN。
- `Viewport_NoLevels_Defaults`：level downsample 列表只有 `[1]`，视口为 0×0。各方法都不抛异常、不产生 NaN。
- `Viewport_ZoomTo_FitsRectangle`。

`TileGridTests`：
- `TileGrid_VisibleTiles_CoversViewExactly`：boxes.tiff level 0（300×250），视窗 (0, 0, 300, 250)，得到 2×1 共 2 个瓦片：(0,0) 为 256×250，(1,0) 为 44×250。
- `TileGrid_Region_UsesLevelDownsample`：level 2（75×62，downsample 4.016129032258064）的 (0,0) 瓦片，Region 返回 (0, 0, 75, 62)。
- `TileGrid_OutsideSlide_NoTiles`：视窗完全在切片之外时为空。

`TileCacheTests`：
- `Cache_StaysWithinBudget`：预算为 1 MiB，依次加入 10 个 256×256 瓦片（每个 256 KiB）。之后 `SizeBytes ≤ 1 MiB`，`Count == 4`，被淘汰的瓦片已 Dispose。
- `Cache_LruOrder`：访问过的瓦片后淘汰。
- `Cache_ConcurrentAccess`：8 个线程交替执行 Add 和 TryGet，不抛异常，且最终仍在预算内。

`TileLoaderTests`（`read` 用可控的假读取器：用信号控制阻塞，并计数）：
- `Loader_LoadsAndNotifies`。
- `Loader_NewGeneration_DropsQueuedRequests`：单个 worker，第一个请求阻塞时再提交 100 个请求，随后换新的一代提交 2 个；最终只读取了 1 + 2 个。
- `Loader_DoesNotDuplicateInFlight`。
- `Loader_DisposeWhileLoading_CompletesQuietly`：read 阻塞时调用 Dispose；释放阻塞后 Dispose 返回，不再触发 loaded，也没有异常逃逸。
- `Loader_FailureStopsFurtherRequests`。

`CheckModeTests`：
- 调用 `Program.RunCheck(["--check", small.svs], stdout)`：返回 0，输出含 `openslide=4.` 和 `slide=16x16`；
- 传入 `unopenable.tiff` 时返回 1；
- `RunCheck` 是 internal 方法，返回退出码。

- [ ] **Step 2: 运行，确认失败**（编译错误）
- [ ] **Step 3: 实现**

csproj 中 Avalonia 设置 `AvaloniaUseCompiledBindingsByDefault=true`。`App` 只放 FluentTheme 和 Inter 字体。

- [ ] **Step 4: 运行全部测试，确认通过**

Run: `dotnet test OpenSlideSharp.slnx -c Release`

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add the viewer's core (viewport, tile grid, cache, loader) and --check mode"
```

---

### Task 2: `SlideView` 控件（渲染、平移缩放、键盘、选区绘制）

**Files:**
- Create:
  - `samples/OpenSlideSharp.Viewer/Controls/SlideView.cs`
  - `samples/OpenSlideSharp.Viewer/Controls/TileBitmapCache.cs`（把 `TileImage` 转为 `WriteableBitmap`，与 `TileCache` 同生命周期）
  - `test/OpenSlideSharp.Viewer.Tests/HeadlessSession.cs`（`HeadlessUnitTestSession.StartNew(typeof(App))` 单例，加上一个在会话中执行的 `Run<T>(Func<T>)` 辅助方法）
  - `test/OpenSlideSharp.Viewer.Tests/SlideViewTests.cs`

**Interfaces:**
- Consumes: Task 1 的 Core 类型。
- Produces：

```csharp
sealed class SlideView : Control
{
    OpenSlide? Slide { get; set; }               // 设置时重建 Viewport/TileGrid/TileCache/TileLoader，并对旧对象 Dispose
    Viewport? Viewport { get; }
    SelectionList Selections { get; }
    (double X, double Y)? PointerSlidePosition { get; } // 光标处 level-0 坐标；光标不在控件上时为 null
    event Action? ViewChanged;                   // 视口或光标变化时触发（状态栏、导航器订阅）
    event Action<Exception>? LoadFailed;
    long TileReads { get; }                      // 诊断/测试用：累计 ReadRegion 次数
}
```

- **Render(DrawingContext)：**
  1. 填充背景色；
  2. 计算目标层级和可见瓦片：
     - 已缓存的瓦片直接绘制；
     - 未缓存的瓦片按坐标从近到远（中心优先）放进请求列表，并按 level+1、level+2… 依次寻找已缓存的更粗层级瓦片，裁出覆盖区域绘制；
  3. 提交请求；
  4. 绘制选区（1 个设备像素的轮廓，颜色 `#FFD000`；正在拖动的选区画虚线）。
- **线程：** Render 中**不得**调用 `ReadRegion`。瓦片加载完成后，通过 `Dispatcher.UIThread.Post(InvalidateVisual)` 合并刷新：同一帧只调度一次。
- **高 DPI：** 以 `VisualRoot.RenderScaling` 计算设备像素。瓦片按目标层级像素与设备像素对齐绘制，使用 `RenderOptions.BitmapInterpolationMode=HighQuality`。
- **输入：**
  - 左键拖动平移；Shift + 左键拖动画选区，松开时宽高都大于 0 才加入；
  - 滚轮按 `delta.Y` 换算为 2^(delta/4)；
  - 键盘按 Global Constraints 的映射处理，控件可获得焦点（`Focusable=true`），按下鼠标时获得焦点。

- [ ] **Step 1: 写失败的测试**（headless 会话，窗口 400×300，内容为 SlideView）
- `SlideView_RendersSlidePixels`：
  - `Slide = boxes.tiff`，等加载完成（轮询 `TileReads > 0` 且没有待处理请求，最多 5 秒，期间不断 `Dispatcher.UIThread.RunJobs()`）；
  - 用 `CaptureRenderedFrame()` 截取画面，至少有 1000 个像素不是背景白色。boxes.tiff 含红、蓝、绿色块。
- `SlideView_RenderDoesNotReadOnUiThread`：用测试替身记录 read 调用所在的线程 ID，所有调用都不在 UI 线程上。实现时可通过 internal 的读取器注入点完成。
- `SlideView_WheelZoomsAtPointer`：在 (100, 80) 滚动 +1 格后，`PointerSlidePosition` 在缩放前后的差值不超过 1 个 level-0 像素。
- `SlideView_DragPans`：拖动 (50, 0) 后，`Viewport.CenterX` 减少 `50 × downsample`。
- `SlideView_ShiftDragAddsSelection`：选区数量变为 1，矩形与拖动范围换算一致。
- `SlideView_Keyboard_ZoomFitAndOneToOne`：按 `1` 后 downsample 为 1；按 `Z` 后与 `Fit()` 的结果相同。
- `SlideView_RenderScaling_UsesPhysicalPixels`：`RenderScaling = 2` 时，截图像素尺寸为 800×600，色块边缘位置换算正确（±2 设备像素）。如果 headless 后端不支持设置 RenderScaling，则在测试中 `Assert.Skip` 并写明原因。
- `SlideView_SwitchSlide_DisposesOldLoader`：从 boxes.tiff 切换到 small.svs 后，旧加载器已 Dispose，缓存已清空。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add the SlideView control with tiled rendering, pan, zoom and selections"
```

---

### Task 3: 主窗口（打开、导航器、属性、关联图像、选区列表、状态栏）

**Files:**
- Create:
  - `samples/OpenSlideSharp.Viewer/MainWindow.axaml(.cs)`
  - `samples/OpenSlideSharp.Viewer/Controls/Navigator.cs`
  - `samples/OpenSlideSharp.Viewer/AssociatedImageWindow.axaml(.cs)`
  - `samples/OpenSlideSharp.Viewer/StatusFormatter.cs`（纯函数，便于测试）
  - `test/OpenSlideSharp.Viewer.Tests/MainWindowTests.cs`、`StatusFormatterTests.cs`
- Modify: `samples/OpenSlideSharp.Viewer/App.axaml.cs`（按命令行参数打开切片）

**Interfaces:**
- **布局：**
  - 顶部菜单：File（Open…、Close、Exit）、View（Fit、1:1、Center）。
  - 左栏（可拖动分隔条）：上部导航器（缩略图 + 视口矩形，单击或拖动跳转），下部 Tab：Properties（按键名排序的两列列表，可复制）、Associated images（列表，双击打开窗口）、Selections（列表，每项带 “Zoom to” 和 “Delete” 按钮）。
  - 中间：`SlideView`。
  - 底部：状态栏。
  - 窗口标题：`OpenSlide Viewer — <文件名>`。
- **`MainWindow.OpenAsync(string path)`：**
  - 先 `OpenSlide.Open`；
  - 失败时显示错误（内嵌对话框或状态栏红字），保留原切片；
  - 成功后替换切片：释放旧 `OpenSlide`，同时清空选区、属性和关联图像。
- **导航器缩略图：** `slide.GetThumbnail(256, 256)`，或缩略图关联图像，先取到哪个用哪个。
- **关联图像窗口：** 显示 `AssociatedImage.Read()` 的结果（预乘 BGRA → WriteableBitmap），按窗口大小等比缩放；标题为图像名。
- **StatusFormatter：**
  - `Coordinates(double? x, double? y) → string`：`"x: 1234  y: 567"`，参数为空时返回 `""`；
  - `Zoom(double downsample, double? objectivePower) → string`：`"1:4.02 (9.96×)"`，没有 objectivePower 时只显示 `"1:4.02"`；
  - `PhysicalSize(double width0, double height0, double? mppX, double? mppY) → string`：
    - 两个 mpp 都有效时返回 `"812.5 × 600.0 µm"`；任一边达到 1000 µm 时改用 mm 并保留 2 位小数，例如 `"1.25 × 0.80 mm"`；
    - 否则返回 `""`。
- **拖放：** 窗口接受拖入的文件，取第一个打开。

- [ ] **Step 1: 写失败的测试**

`StatusFormatterTests`：
- 坐标：`Coordinates(null, null) == ""`，`Coordinates(1234.6, 567.2) == "x: 1234  y: 567"`（向下取整）。
- 缩放：`Zoom(4.016129, 40) == "1:4.02 (9.96×)"`；`Zoom(1, null) == "1:1.00"`。
- 物理尺寸：
  - `PhysicalSize(1625, 1200, 0.5, 0.5) == "812.5 × 600.0 µm"`；
  - `PhysicalSize(5000, 3200, 0.25, 0.25) == "1.25 × 0.80 mm"`；
  - `PhysicalSize(10, 10, null, 0.5) == ""`；
  - `StatusBar_NoMpp_OmitsPhysicalSize`：`PhysicalSize(800, 600, null, null) == ""`，且 `PhysicalSize(800, 600, double.NaN, 0.5) == ""`。

`MainWindowTests`（headless）：
- `Open_ShowsPropertiesSorted`：打开 small.svs 后 Properties 列表包含 `openslide.vendor`，且按键名排序。
- `Open_ListsAssociatedImages`：small.svs 的 Associated images 列表含 `thumbnail`；打开它的窗口后，窗口标题为 `thumbnail`，图像为 16×16。
- `Navigator_ClickCentersView`：在导航器上点击 (10, 10) 后，主视口中心按缩略图比例换算正确（±1 level-0 像素 × 缩略图比例）。
- `Selections_ZoomTo`：加入一个选区后点击 “Zoom to”，视口完整包含该选区，且至少一边贴合（±1 逻辑像素）。
- `Open_UnopenableFile_ShowsErrorAndKeepsRunning`：先打开 boxes.tiff，再打开 unopenable.tiff；错误文本中含文件名，原切片仍在显示，属性列表不变。
- `SwitchSlide_DoesNotShowOldTiles`：从 boxes.tiff 切换到 small.svs 并等待加载完成。截图中不出现 boxes 的纯红块：红色像素少于 10 个。small.svs 不含纯红。
- `Title_ShowsFileName`。

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行全部测试，确认通过**；再手动运行一次 `dotnet run --project samples/OpenSlideSharp.Viewer -- test/OpenSlideSharp.Tests/Fixtures/boxes.tiff`，用截图确认窗口正常显示，然后关闭。在报告里附上截图的保存路径（放在临时目录中）。
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add the viewer window with navigator, properties, associated images and selections"
```

---

### Task 4: 自包含发布冒烟、CI 与文档

**Files:**
- Modify:
  - `eng/Test-Packages.ps1`
  - `.github/workflows/ci.yml`（如需要）
  - `README.md`
- Create: `samples/OpenSlideSharp.Viewer/README.md`

**Interfaces:**
- **`Test-Packages.ps1` 新增查看器发布冒烟**，在所有平台的非 `-SkipPack` 和 `-SkipPack` 运行中都执行：
  - 发布命令：`dotnet publish samples/OpenSlideSharp.Viewer -c Release -r <宿主 RID> --self-contained -p:PublishSingleFile=true -p:UseOpenSlideRuntimePackage=true -p:OpenSlideSharpVersion=<v>`；
    - 还原源加上本地 feed，`NUGET_PACKAGES` 指向隔离缓存；
    - 宿主 RID 为 win-x64、linux-x64、linux-arm64、osx-arm64 之一，与 PackageSmoke 的判定方式相同。
  - 运行 `<publish>/OpenSlideSharp.Viewer[.exe] --check <small.svs>`，断言退出码为 0，并输出 `slide=16x16`。
  - 单文件发布时，原生库的位置要么在可执行文件旁边，要么启用 `IncludeNativeLibrariesForSelfExtract=true`。选哪种由实现者决定：应选计划 5 发布时更方便的那种，并写进 README。
- **`ci.yml`：** 原有的 package 和 package-unix 已经运行 `Test-Packages.ps1`，所以无需新增 job。如果发布耗时导致 job 超过 30 分钟，再调大 timeout。
- **文档：**
  - 根 README 增加一节 “Desktop viewer”：运行方式和快捷键表，保持简洁；
  - 查看器 README 写明：
    - 功能与快捷键；
    - 不做 ICC 转换；
    - Windows 上的已知崩溃；
    - `--check` 模式；
    - 自包含发布命令（计划 5 的 Release 会附上 win-x64、linux-x64、osx-arm64 三个构建）。

- [ ] **Step 1: 扩展 `Test-Packages.ps1`，本机运行**

Run: `pwsh eng/Get-OpenSlide.ps1 -Platform all; pwsh eng/Test-Packages.ps1`
Expected: `All package smoke tests passed.`，输出中包含查看器 `--check` 的成功行。

- [ ] **Step 2: 更新文档**
- [ ] **Step 3: 运行全部测试，提交**

```bash
git add -A && git commit -m "ci: publish the viewer self-contained and check it on every platform; document it"
```
