# 计划 1 遗留事项（供计划 2–5 使用）

来源：计划 1（核心绑定与 runtime 包）各任务审阅，以及整个分支终审的分级结果。下列事项都已评估为**不阻塞计划 1 合并**，按应在哪个计划处理分组。

## 计划 2（渲染）开始前或进行中必须决定或处理

- **`PixelImage` 构造要求 `data.Length` 恰好等于 `width*height*4`**：这与 `ArrayPool` 冲突。需要决定是否支持 `Memory<byte>` 或更长的缓冲区。改动时一并处理 `Stride` 的 int 溢出，以及不校验 `Format` 的问题。
- **ICC 字节数组是共享的**：`OpenSlide.ColorProfile` 与每个 `PixelImage.IccProfile` 指向同一个 `byte[]`，任何人改了都会影响全部。设计 `IColorTransform` 和 Skia 时要定下来：靠只读约定，还是改用 `ReadOnlyMemory<byte>`。
- **`Properties` 是“活”的**，每次访问都会查询原生层。生成器应在构造时把 bounds 和 background-color 读出来缓存。
- **DeepZoom / OME-Zarr 需要一个单层“源”抽象**，供 `AssociatedImage` 复用。
- **`Unpremultiply` 在 `c > a` 时截断到 255**，而 Python 是回绕。写进文档或补测试。
- **span 版 `ReadRegion` 对目标缓冲区的对齐没有保证**（cairo 要求 malloc 级对齐）。写进文档。
- **规格文档 §6.1/§8 写的是 `OpenSlide.LibraryVersion`**，实际实现是 `OpenSlideLibrary.Version`。

## 计划 3/4（Web、工具、查看器）

- 示例项目导入 `eng/OpenSlideNative.targets` 时，该文件目前只检查目录是否存在，空目录也会通过。
- `AssociatedImages` 遇到含 NUL 的键返回“未找到”，`Properties` 则抛 `ArgumentException`。两者语义不一致，需要统一或写进文档。
- `SetLibraryPath` 和 `OPENSLIDE_LIBRARY_PATH` 不拒绝内嵌 NUL。
- win-arm64 上 x64 DLL 无法加载，目前也没有任何警告。

## 计划 5（发布）之前必须处理

- **`Test-Packages.ps1` 下载的是 `latest` 的 nuget.exe**，没有固定版本，也没有校验哈希。发布 job 会持有 `NUGET_API_KEY`，所以必须先修。
- **本地打包可能混入不同版本的原生库**：`Get-OpenSlide -Platform current` 只刷新当前平台。打包前应校验 `.stamp` 与清单的 sha 一致。
- **`Update-OpenSlideManifest.ps1` 采用首次信任（TOFU）记录 sha256**。应与 GitHub release asset 的 `digest` 字段交叉校验。
- **nightly 源码包只保留约 6 个月**，影响 LGPL 合规：清单需要增加 source 资源，发布时把源码包附到 GitHub Release。
- **`VERSIONS.md` / `versions.json` 实际取自最后处理的那个归档**（macOS）。需要选定一个确定的来源。
- **原生 `PackageReference`（VS 2026 18.7+）路径从未测试过**：要么补测试，要么在 README 里注明。
- **README 的 Known issues 链接指向 master**，合并到 master 之前会 404。根 README 需要整体重写。
- **CI**：
  - 没有 `concurrency` 分组和 `cancel-in-progress`，也没有 `timeout-minutes`。
  - `::error::` 文本中英混杂。
  - `ubuntu-24.04-arm` 只对公开仓库免费。

## 可以继续延后（低优先级）

- **脚本与构建**：
  - `Get-OpenSlide` 读取 404 状态码时没有防护，提示里也可以提一下 `-Channel` 参数；
  - `Generate-Bindings` 的报错信息可以更清楚；
  - `NoWarn CS8981` 作用于整个项目；
  - `IsSrcProject` 的反例没有验证，`StartsWith` 受文化设置影响。
- **测试强度**：
  - `Assert.StartsWith` 没有指定 `Ordinal`；
  - 只断言异常类型，不断言原生错误文本；
  - `PropertyTests` 偏弱；
  - 没有测跨 `Dispose` 持有 `AssociatedImage` 的情况；
  - 终结器路径没有测试；
  - 解析器的显式路径与环境变量语义没有子进程级测试。
- **代码细节**：
  - `Properties` 的 getter 本身不检查可用性，与文档措辞不一致；
  - `?? string.Empty`；`Keys` 返回可变 `List`；`Count` 是 O(n)；
  - 名称按 UTF-8 有损解码；
  - ICC 缓存逻辑重复了一份；
  - `AssociatedImage.Read()` 抛出的 `ArgumentOutOfRangeException` 参数名是不存在的 `width`；
  - lease 的后置检查会掩盖回调自身的异常（应写进 `AcquireHandle` 的文档）；
  - 先分配数组，再检查对象是否已释放。
- **打包与测试工程**：
  - vcxproj 的 import 没有 `Exists` 守卫，也没有检查 vswhere 或 VC 工具；
  - `OpenSlideCopyNative` 没有按 `ConfigurationType` 限定；
  - `slidetool` 的正则偏松；
  - packages 路径用的是原始版本号，而非规范化版本号；
  - 系统装有 libopenslide 时，no-runtime 变体会误报；
  - `NUGET_PACKAGES` 用完没有还原；
  - 原生文件列表写了两份；
  - `Get-AssemblyArch` 先判断 `Requires32Bit` 再判断 `Prefers32Bit`，导致 anycpu32bitpreferred 被误标为 x86；
  - 跨 ProjectReference 时，net48 类库会和 NuGet 各拷一次同一个 DLL（发生在构建阶段，无害）；
  - 旧式 csproj 默认 `Prefer32Bit=true`，README 可以补一句说明。
- **其他**：
  - `PackageReadmeFile` 对不打包的项目也设置了；
  - 已知问题文档里对上游修复的措辞，以及链接文字。

## 已知问题（维护者决定只写文档）

Windows 上遇到损坏的 JPEG/PNG 会触发 CFG/longjmp 崩溃，详见 `docs/known-issues/windows-cfg-longjmp-crash.md`。不提供运行时补丁，也暂时不向上游提 issue。
