# 计划 3 遗留事项（供计划 4–5 使用）

来源：计划 3（ASP.NET Core 端点、Web 示例、命令行工具）各任务审阅，以及整体终审与修复轮。下列事项都已评估为**不阻塞计划 3 合并**。计划 2 遗留事项中属于计划 3 的部分已处理，包括：
- 告警、Default 编码永不抛异常；
- 生成器按切片缓存；
- key 长度上限与 `ContainsKey`；
- 关联图像强制 `limitBounds=false`；
- README 颜色模式表。

## 计划 5（首次发布）之前必须处理

- **工具输出目录是符号链接或 junction 时会误删链接目标里的内容。** 位置：`ExportSupport.MoveAside` 与 `DeleteMarkedDirectory`。
  - 现象：`Directory.Move` 移走的是链接本身，随后的枚举删除却会穿过链接，清空目标目录。
  - 前提：链接目标里带有 `.openslide-sharp-output` 标记。
  - 修法：在 `EnsureReplaceableDirectory` 中直接拒绝 `LinkTarget != null` 的输出根，或对链接只删除链接本身。
- **工具包的 SPDX 许可表达式是简化写法**：`MIT AND LGPL-2.1-only AND BSD-3-Clause`。随包分发的组件还涉及 MPL、IJG、zlib、FTL 等许可，全文都在包内 `licenses/`。发布前决定是否列全，或改用 `PackageLicenseFile`。runtime 包同样需要评估。
- **打包顺序**：工具在打包时要求 `UseOpenSlideRuntimePackage=true`，且 runtime 包必须已在源中，所以对整个解决方案执行 `dotnet pack` 会被拒绝。发布流程必须照 `eng/Test-Packages.ps1` 的顺序：先打 core、Skia、runtime、AspNetCore，再打工具。
- **体积**：工具包约 36 MB，其中 5 个 RID 的 libopenslide 和 4 个平台的 SkiaSharp 原生库占大头。CI 的 `packages` artifact 也随之变大。
- **公开 API 的最后调整窗口**（发布后再改就是破坏性变更）：
  - 提供能拿到 `HttpContext` 的异步 resolver 扩展点，供按切片鉴权使用；
  - 支持具名 options，或给 `Map*` 加带选项的重载，让同一应用能挂两组参数不同的端点；
  - 公开 `SlideCache.Evict(SlideLease)`。现在公开的 `Evict(string)` 按路径淘汰，自定义端点可能误淘汰并发重开的新实例。

## 计划 4（桌面查看器）注意

- 查看器直接使用核心 API（`OpenSlide`、`ReadRegion`、`GetBestLevelForDownsample`）。不经过 AspNetCore 层，也不需要 SkiaSharp 编码包：Avalonia 自带 Skia，SkiaSharp 版本为 3.119.4，与仓库固定的版本一致。
- 在 `OpenSlideSharp.*` 命名空间中引用 Skia 类型时，用文件顶部的 `using SkiaSharp;`，不要写 `SkiaSharp.X` 限定名（R11）。
- Windows 上的已知崩溃（CFG/longjmp）同样会让查看器进程退出：遇到损坏的切片时进程会直接终止。README 要写明。

## 可以继续延后（低优先级）

- **服务端：**
  - Default 模式下，第 1、2 级转换器在渲染时抛出的异常会变成未处理的 500（R10）。只在 Skia 内部失败时发生，基本等同于内存耗尽。
  - CORS 头不覆盖路由层的 405 和未处理异常。
  - `TryGetGenerator` 的 catch 仍然包住了生成器构造，可以改为 `SlideEntry.TryGet…`。
  - `ContentType()` 重复维护了一份元数据 key 列表。
  - `MaxKeyLength` 在类中的位置；DZ 的粘滞错误测试放在了 OME 测试文件里。
  - 首次打开与 `Dispose` 竞争时，共享 tile cache 可能只能靠 finalizer 回收。
  - CORS 回显请求头的注释说法不准确。
- **Web 示例：**
  - 单片模式启动时不检查文件是否为切片；`wwwroot/lib` 不随 clean 清理；有一段 `Condition="false"` 死代码。
  - 列表：`LinkTarget` 没有异常保护；扫描在锁内进行，Unix 上遇到 FIFO 可能阻塞。
  - PathBase 未转义；无扩展名的许可证文件不被静态文件中间件提供。
  - `-l` 不支持 IPv6；`-h` 以退出码 2 结束；单片模式下仍显示 “All slides” 链接；返回 2 的路径上没有 dispose `WebApplication`。
- **工具：**
  - 输出目录本身是挂载点时，rename 失败，导致永远无法替换，且提示有误导。可以回退为原地删除，或在文档中说明。
  - `omezarr --associated` 中关联目录 MoveAside 失败时，提示 “nothing was changed” 不准确。
  - 非 `-r` 模式下 `.dzi` 删除失败时，会留下指向已移走目录的 `.dzi`。
  - OME 进度把元数据也计入 “tiles”；“embedding” 的告警措辞不准确。
  - 与 Python 的差异：进度文本没有 “wrote”；默认 `-j` 为处理器数，Python 为 4。
- **CI：**
  - 启动 Web 示例的 curl 没有 `--max-time`；停止步骤在进程退出前就打印了日志。
  - `verify-omezarr.py --url` 不带 `--manifest` 时，查不出返回 404 的 chunk。
  - pip 依赖只固定了版本，没有锁哈希。
- **测试缺口：**
  - 缓存：`TryAcquire` 与 `Dispose` 的竞争。
  - 端点：Default 模式 + 无法转换 ICC 的端到端 ICC 嵌入；HEAD 返回 404 的各种情况；断言并发测试中确实发生了淘汰。
  - CORS：带 Origin 的真实预检；500 响应上的 CORS 头。
  - 工具：颜色模式；worker 失败的传播。
  - 示例：`-B` 的端到端效果；文件名中特殊字符的转义；列表缓存。
- **Trim/AOT：** 端点使用委托处理器（`MapMethods(Delegate)`），在 trimming 和 AOT 下会产生警告。配置绑定重载已标注 `RequiresUnreferencedCode` 和 `RequiresDynamicCode`。

## 计划 3 中做出的裁定（摘要）

- **R1**：Task 2 的 TestApp 只挂 DeepZoom，OME-Zarr 在 Task 3 加入。
- **R2**：用一个 `AddOpenSlide` 注册，代替规格草图中的 `AddOpenSlideDeepZoom`。
- **R3**：Default 模式下 ICC 无法转换时，编码使用 `ColorMode.Default`，退化为嵌入，过大的 profile 则丢弃。
- **R4**：开放 `OmeZarr.RenderCacheSize` 配置项。
- **R5**：计划中的测试 URL 笔误以 URL 语法为准。
- **R6**：粘滞错误测试、收窄 catch、一次性创建 logger，三项并入 Task 3。
- **R7**：OME 路径在最后一个 `.zarr/` 处切分。
- **R8**：工具只替换带 `.openslide-sharp-output` 标记的输出目录。
- **R9**：OME 根元数据最后写入。
- **R10**：服务端颜色转换器抛出的异常暂不处理。
- **R11**：增加全局 `TileCacheSize`（默认 128 MiB）。
- **R12**：工具包只保留 OpenSlide 支持平台的 SkiaSharp 原生库。

## 已知问题（维护者决定只写文档）

Windows 上遇到损坏的 JPEG/PNG 会触发 CFG/longjmp 崩溃，详见 `docs/known-issues/windows-cfg-longjmp-crash.md`。
