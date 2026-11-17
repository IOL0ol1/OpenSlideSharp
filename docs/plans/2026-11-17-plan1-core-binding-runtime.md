# Plan 1：核心绑定与 runtime 包 Implementation Plan

**Goal:** 把仓库重置为基于 OpenSlide 4.x 的新结构，交付可用的 `OpenSlideSharp` 核心包（ClangSharp 生成的底层 + 手写高层 API）和 `OpenSlideSharp.runtime` 原生包（.NET 四平台 + VS C++），并有四平台 CI 守护。

**Architecture:** 上游构建由 `eng/openslide.json` 清单固定，脚本按清单下载并校验到 `artifacts/native/`。ClangSharp 从清单对应的 `openslide.h` 生成 `OpenSlideSharp.Native.openslide`（保留 C 名，提交进仓库）；手写层用 `SafeHandle` 包装句柄，每次调用后检查 `openslide_get_error`，语义对齐 openslide-python。runtime 包只打包文件，不含代码。

**Tech Stack:** .NET 10 SDK、C#（LangVersion latest）、ClangSharpPInvokeGenerator 21.1.8.2、xUnit v3、PowerShell 7（`pwsh`）脚本、MSBuild/MSVC（C++ 冒烟测试）、GitHub Actions。

**Spec:** `docs/design/2026-11-17-openslidesharp-v4-refactor-design.md`（路线图：`docs/plans/2026-11-17-openslidesharp-v4-roadmap.md`）

## Global Constraints

- 核心包目标框架：`netstandard2.0;netstandard2.1;net10.0`；测试与冒烟项目 `net10.0`（Windows 上冒烟额外 `net48`）。
- 核心包零依赖：唯一允许的包依赖是 `System.Memory`，且仅限 `netstandard2.0`。核心包 **不** 依赖 `OpenSlideSharp.runtime`。
- `DllImport` 库名 `libopenslide-1`。runtime 包内文件名：win-x64 `libopenslide-1.dll`；linux-x64 / linux-arm64 `libopenslide-1.so`；osx-arm64 / osx-x64 同一个 universal `libopenslide-1.dylib`。Windows DLL 不改名。
- 生成层：命名空间 `OpenSlideSharp.Native`，类 `openslide`，保留 C 函数名（`--prefixStrip openslide_`），文件 `src/OpenSlideSharp/Native/OpenSlide.Native.g.cs` 提交进仓库。
- 手写高层只封装最新 OpenSlide 正式版（4.0.1）头文件中已有的 API。
- 所有字符串（含 Windows 文件名）按 UTF-8 编解码。
- runtime 包：不含 `.debug` / `.dSYM` / `slidetool`；附带 openslide-bin 的 `licenses/`、`VERSIONS.md`、`versions.json`；`PackageLicenseExpression` 为 `LGPL-2.1-only`；tags 含 `native`。
- 清单下载必须校验 sha256；nightly 资源 404 时给出“改用最新构建”的提示。
- 托管代码 MIT；包元数据沿用 `Authors=IOL0ol1`、图标 `openslide_logo.png`、仓库 `https://github.com/IOL0ol1/OpenSlideSharp`。
- 本地版本号默认 `0.0.0-local`，CI / 发布用 `-p:Version=` 覆盖（版本计算属于计划 5）。

## Review Focus

1. 一个线程 `Dispose()` 的同时其他线程正在 `ReadRegion`（瓦片服务器的常态）：不得崩溃或访问已释放的句柄，只允许抛 `ObjectDisposedException`。→ Task 7 `ReadRegion_ConcurrentWithDispose_OnlyObjectDisposed`
2. Windows 上含中文的路径（本项目维护者的常见环境）：`DetectFormat` / `Open` 正常工作。→ Task 5 `Open_ChineseDirectoryAndFileName_Works`
3. 超大区域（`width*height*4 > int.MaxValue`）调用数组版 `ReadRegion`、或 `Span` 目标过小：抛 `ArgumentOutOfRangeException` / `ArgumentException`，不能溢出或写越界。→ Task 7 `ReadRegion_TooLargeForArray_Throws`、`ReadRegion_SpanTooSmall_Throws`
4. 用户只引用 `OpenSlideSharp` 而没有原生库：异常信息要指明“引用 OpenSlideSharp.runtime 或设置 OPENSLIDE_LIBRARY_PATH”，而不是裸的 `DllNotFoundException`。→ Task 9 冒烟测试的 no-runtime 变体
5. 切片进入粘滞错误状态后：`Dispose()` 与终结器仍安静地完成，不抛异常。→ Task 6 `Dispose_AfterStickyError_DoesNotThrow`

---

### Task 1: 仓库重置与新骨架

**Files:**
- Delete: `src/OpenSlideSharp.Interop/`、`src/OpenSlideSharp.SourceGenerator/`、`src/OpenSlideSharp.BitmapExtensions/`、`src/OpenSlideSharp.OpencvExtensions/`、`src/OpenSlideSharp.BruTile/`、`src/OpenSlideSharp.Windows/`、`src/OpenSlideSharp.runtime.win/`、`src/OpenSlideSharp/` 下全部旧文件（`AssociatedImage.cs`、`DeepZoomGenerator.cs`、`ImageDimension.cs`、`OpenSlideException.cs`、`OpenSlideImage.cs`、`Interop/*`、旧 csproj）、`example/`、`build/`、`generation/`（含 21MB zip；`GenerateOpenSlide.rsp` 在 Task 3 移到 `eng/`，先 `git mv` 再改）、`test/OpenSlideSharp.Tests/` 全部旧内容、`OpenSlideSharp.sln`、`OpenSlideSharp.Common.props`、`.github/workflows/generate-and-publish.yml`
- Move: `dotnet-tools.json` → `.config/dotnet-tools.json`（内容不变，ClangSharpPInvokeGenerator 21.1.8.2）
- Create: `global.json`、`Directory.Build.props`、`Directory.Packages.props`、`OpenSlideSharp.slnx`、`src/OpenSlideSharp/OpenSlideSharp.csproj`、`src/OpenSlideSharp/Internal/Polyfills.cs`
- Modify: `.gitignore`（追加 `artifacts/`）

**Interfaces:**
- Produces: `Directory.Build.props` 提供的公共属性（后续所有项目继承）：`LangVersion=latest`、`Nullable=enable`、`ImplicitUsings=disable`、`ArtifactsPath=$(MSBuildThisFileDirectory)artifacts`（构建输出在 `artifacts/bin`、`artifacts/obj`，包在 `artifacts/package/release`）、`Version` 默认 `0.0.0-local`、包元数据；`src/**` 项目 `TreatWarningsAsErrors=true`、`GenerateDocumentationFile=true`。
- Produces: `Polyfills.cs`（`#if !NET`）：`System.Runtime.CompilerServices.IsExternalInit`、`System.Diagnostics.CodeAnalysis.MaybeNullWhenAttribute`、`NotNullWhenAttribute`，均为 `internal`。

- [ ] **Step 1: 执行删除与移动**

```bash
git rm -r -q src/OpenSlideSharp.Interop src/OpenSlideSharp.SourceGenerator src/OpenSlideSharp.BitmapExtensions src/OpenSlideSharp.OpencvExtensions src/OpenSlideSharp.BruTile src/OpenSlideSharp.Windows src/OpenSlideSharp.runtime.win src/OpenSlideSharp example build test/OpenSlideSharp.Tests OpenSlideSharp.sln OpenSlideSharp.Common.props .github/workflows/generate-and-publish.yml
mkdir -p eng .config && git mv generation/GenerateOpenSlide.rsp eng/GenerateOpenSlide.rsp && git rm -r -q generation && git mv dotnet-tools.json .config/dotnet-tools.json
```

- [ ] **Step 2: 写 `global.json`**（`{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}`）、`Directory.Build.props`（上面的公共属性；`Version` 用 `<Version Condition="'$(Version)' == ''">0.0.0-local</Version>`；`openslide_logo.png` 与包 README 以 `None Pack=true PackagePath=\` 方式加入，仅对 `IsPackable` 为 true 的项目）、`Directory.Packages.props`（`ManagePackageVersionsCentrally=true`；登记 `System.Memory`、`xunit.v3`、`xunit.runner.visualstudio`、`Microsoft.NET.Test.Sdk` 的最新稳定版）**

- [ ] **Step 3: 写 `src/OpenSlideSharp/OpenSlideSharp.csproj`**：`TargetFrameworks=netstandard2.0;netstandard2.1;net10.0`、`AllowUnsafeBlocks=true`、`PackageId=OpenSlideSharp`、Description `.NET bindings for OpenSlide 4.x (https://openslide.org/), with Deep Zoom and OME-Zarr generators.`；`System.Memory` 仅 `netstandard2.0` 引用；加入 `Internal/Polyfills.cs`。

- [ ] **Step 4: 用 `dotnet new sln --format slnx -n OpenSlideSharp` 建解决方案并 `dotnet sln add src/OpenSlideSharp/OpenSlideSharp.csproj`**

- [ ] **Step 5: 验证**

Run: `dotnet build OpenSlideSharp.slnx -c Release`
Expected: `Build succeeded`，0 warnings，0 errors。
Run: `git ls-files src example build generation | head`
Expected: 只剩 `src/OpenSlideSharp/OpenSlideSharp.csproj` 与 `src/OpenSlideSharp/Internal/Polyfills.cs`。

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "chore: reset repository for OpenSlide 4.x rewrite"
```

---

### Task 2: 上游清单与下载脚本

**Files:**
- Create: `eng/Update-OpenSlideManifest.ps1`、`eng/Get-OpenSlide.ps1`、`eng/openslide.json`（由脚本生成后提交）

**Interfaces:**
- Produces: 清单格式（计划 5 的同步流程沿用）：

```json
{
  "channel": "nightly",
  "binVersion": "4.0.1.2+20261102.nightly",
  "openslideVersion": "v4.0.1-49-g174630bf",
  "openslideCommit": "174630bf",
  "date": "2026-11-02",
  "releaseUrl": "https://github.com/openslide/builds/releases/tag/v4.0.1.2%2B20261102.nightly",
  "assets": {
    "windows-x64":        { "url": "…-windows-x64.zip",            "sha256": "…" },
    "linux-x86_64":       { "url": "…-linux-x86_64.tar.xz",        "sha256": "…" },
    "linux-aarch64":      { "url": "…-linux-aarch64.tar.xz",       "sha256": "…" },
    "macos-arm64-x86_64": { "url": "…-macos-arm64-x86_64.tar.xz",  "sha256": "…" }
  }
}
```

- Produces: `eng/Update-OpenSlideManifest.ps1 [-Channel nightly|stable] [-Tag <tag>] [-OutFile eng/openslide.json]`
  - nightly 取 `openslide/builds` 最新带资源的 prerelease；stable 取 `openslide/openslide-bin` 最新非 prerelease。
  - 下载四个归档到 `artifacts/downloads/`，计算 sha256。
  - 从 windows zip 的 `versions.json` 读出 `id == "openslide"` 那一项的 `version`，作为 `openslideVersion`。
  - `openslideCommit`：nightly 从 `-g([0-9a-f]+)$` 取前 8 位；stable 通过 GitHub API 解析 `openslide/openslide` 的 tag `v<version>`。tag 是 annotated 的，要再查一次 `git/tags/{sha}` 取得 commit，最后截前 8 位。
  - `date`：nightly 取 `+YYYYMMDD`；stable 取 release 的 `published_at`。
  - 设置了 `GITHUB_TOKEN` 环境变量时带上认证头。
- Produces: `eng/Get-OpenSlide.ps1 [-Platform current|all|<archive-key>[,…]] [-Manifest eng/openslide.json] [-Destination artifacts/native] [-Force]`，默认 `current`。输出布局：

```
artifacts/native/
  win-x64/libopenslide-1.dll          ← windows zip  bin/libopenslide-1.dll
  linux-x64/libopenslide-1.so         ← linux-x86_64 lib/libopenslide.so.1.* 的实体文件（排除 .debug 与符号链接）
  linux-arm64/libopenslide-1.so       ← linux-aarch64 同上
  osx-arm64/libopenslide-1.dylib      ← macos lib/libopenslide.1.dylib
  osx-x64/libopenslide-1.dylib        ← 同一文件
  msvc/x64/libopenslide.lib           ← windows zip lib/libopenslide.lib
  include/openslide/openslide.h, openslide-features.h
  licenses/…  VERSIONS.md  versions.json  CHANGELOG.md   ← 任一已处理的归档
  .stamp                               ← 已展开的 asset sha256 列表；未变化且无 -Force 时跳过
```

`current` 根据 `$IsWindows` / `$IsLinux` / `$IsMacOS` 和 `RuntimeInformation.OSArchitecture` 选出唯一的归档。解压统一用 `tar -xf`：Windows 自带的 bsdtar 支持 zip 与 xz，GNU tar 支持 xz。

- [ ] **Step 1: 实现 `Update-OpenSlideManifest.ps1`，生成清单**

Run: `pwsh eng/Update-OpenSlideManifest.ps1 -Channel nightly`
Expected:
- 写出 `eng/openslide.json`，`binVersion` 匹配 `^\d+\.\d+\.\d+\.\d+\+\d{8}\.nightly$`；
- `openslideCommit` 为 8 位十六进制；
- 四个 asset 都带 64 位 sha256。

执行时如果上游已经有更新的 nightly，以新的为准。

- [ ] **Step 2: 实现 `Get-OpenSlide.ps1`，下载全部平台**

Run: `pwsh eng/Get-OpenSlide.ps1 -Platform all; Get-ChildItem -Recurse artifacts/native -File | % { $_.FullName.Substring((Resolve-Path artifacts/native).Path.Length) }`
Expected:
- 列出上面布局中的全部文件；
- 不含任何 `.debug`、`.dSYM` 或 `slidetool` 文件。

- [ ] **Step 3: 验证校验失败路径**

Run（在 pwsh 会话中直接执行，不要包在 bash 的双引号里）:

```powershell
$m = Get-Content eng/openslide.json | ConvertFrom-Json; $m.assets.'windows-x64'.sha256 = '0' * 64
$m | ConvertTo-Json -Depth 5 | Set-Content artifacts/bad.json
./eng/Get-OpenSlide.ps1 -Platform windows-x64 -Manifest artifacts/bad.json -Force
```

Expected:
- 非零退出，错误信息含 `SHA256 mismatch`；
- `artifacts/downloads` 下该 zip 被删除。

- [ ] **Step 4: 验证 404 提示**

Run（pwsh）:

```powershell
$m = Get-Content eng/openslide.json | ConvertFrom-Json; $m.assets.'windows-x64'.url += '.missing'
$m | ConvertTo-Json -Depth 5 | Set-Content artifacts/gone.json
./eng/Get-OpenSlide.ps1 -Platform windows-x64 -Manifest artifacts/gone.json -Force
```

Expected: 非零退出，信息同时包含 `no longer available` 和 `Update-OpenSlideManifest.ps1`。

- [ ] **Step 5: Commit**

```bash
git add eng/Update-OpenSlideManifest.ps1 eng/Get-OpenSlide.ps1 eng/openslide.json && git commit -m "build: add upstream OpenSlide manifest and download scripts"
```

---

### Task 3: 生成底层绑定并搭好测试项目

**Files:**
- Modify: `eng/GenerateOpenSlide.rsp`（来自 Task 1 的 `git mv`）
- Create: `eng/GeneratedHeader.txt`、`eng/Generate-Bindings.ps1`、`eng/OpenSlideNative.targets`
- Create: `src/OpenSlideSharp/Native/OpenSlide.Native.g.cs`（生成）、`src/OpenSlideSharp/Native/openslide.cs`、`src/OpenSlideSharp/Native/NativeTypeNameAttribute.cs`
- Create: `test/OpenSlideSharp.Tests/OpenSlideSharp.Tests.csproj`、`test/OpenSlideSharp.Tests/TestFiles.cs`、`test/OpenSlideSharp.Tests/Fixtures/*`、`test/OpenSlideSharp.Tests/Fixtures/README.md`、`test/OpenSlideSharp.Tests/NativeBindingTests.cs`

**Interfaces:**
- Produces: `OpenSlideSharp.Native.openslide`（public static unsafe partial），成员与 C API 一一对应，签名示例：
  - `byte* detect_vendor(byte* filename)`、`IntPtr open(byte* filename)`
  - `void read_region(IntPtr osr, uint* dest, long x, long y, int level, long w, long h)`
  - `long get_icc_profile_size(IntPtr osr)`、`void read_icc_profile(IntPtr osr, IntPtr dest)`
  - `byte** get_property_names(IntPtr osr)`、`IntPtr cache_create(UIntPtr capacity)`、`byte* get_version()`
  - 常量形如 `ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_VENDOR`，以 NUL 结尾。
- Produces: `eng/OpenSlideNative.targets`：把 `$(MSBuildThisFileDirectory)../artifacts/native/$(NETCoreSdkRuntimeIdentifier)/*` 复制到输出目录。测试项目、以及计划 2–4 的示例项目都导入它。目录不存在时报错，提示先运行 `eng/Get-OpenSlide.ps1`。
- Produces: 测试辅助 `TestFiles.Get(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name)`。

- [ ] **Step 1: 改 rsp 并实现生成脚本**
  - rsp 选项集保持不变，只改以下几项：
    - `--file-directory ../artifacts/native/include/openslide`
    - `--include-directory ../artifacts/native/include/openslide`
    - `--namespace OpenSlideSharp.Native`
    - `--output ../src/OpenSlideSharp/Native/OpenSlide.Native.g.cs`
    - 启用 `--headerFile ./GeneratedHeader.txt`
  - `GeneratedHeader.txt` 的内容：`// <auto-generated/>`，加一行 `#pragma warning disable CS1591`，再加一行说明“由 eng/Generate-Bindings.ps1 从 openslide.h 生成，勿手改”。
  - `Generate-Bindings.ps1` 依次执行：`Get-OpenSlide.ps1 -Platform current`、`dotnet tool restore`，然后在 `eng/` 目录下运行 `dotnet ClangSharpPInvokeGenerator "@GenerateOpenSlide.rsp"`。生成器输出中只要出现 `Error:` 就判为失败。

- [ ] **Step 2: 写辅助文件**
  - `Native/openslide.cs` 是手写的 partial：给 `openslide` 加 XML 文档“Raw P/Invoke bindings generated from openslide.h; prefer the OpenSlide class”，并加 `[EditorBrowsable(EditorBrowsableState.Advanced)]`。
  - `NativeTypeNameAttribute.cs`：`internal sealed class NativeTypeNameAttribute(string name) : Attribute`，带 `[Conditional("DEBUG")]`，`AttributeUsage` 与 ClangSharp 文档一致。

- [ ] **Step 3: 运行生成并确认可重复**

Run: `pwsh eng/Generate-Bindings.ps1; git status --porcelain src/OpenSlideSharp/Native; pwsh eng/Generate-Bindings.ps1; git diff --stat`
Expected:
- 第一次运行后出现新文件 `OpenSlide.Native.g.cs`，包含 24 个 `public static extern` 和 13 个 `OPENSLIDE_PROPERTY_NAME_*`；
- 第二次运行后 `git diff --stat` 为空。

- [ ] **Step 4: 搭建测试项目和测试素材**
  - csproj：`net10.0`，`IsPackable=false`；引用 `xunit.v3`、`xunit.runner.visualstudio`、`Microsoft.NET.Test.Sdk`；`ProjectReference` 到核心项目；导入 `../../eng/OpenSlideNative.targets`；`Fixtures\**` 设为 `CopyToOutputDirectory=PreserveNewest`。
  - 素材：从 `https://raw.githubusercontent.com/openslide/openslide-python/c8c96d5f7fe9fa2cca14845bdd43826390163f2b/tests/fixtures/<name>` 下载以下文件：`boxes.tiff`、`boxes_0.dcm`、`boxes_1.dcm`、`small.svs`、`unopenable.tiff`、`unreadable.svs`、`😐.svs`。
  - `Fixtures/README.md` 注明来源 commit 和 LGPL-2.1 许可。
  - 加入解决方案：`dotnet sln add test/OpenSlideSharp.Tests`。

- [ ] **Step 5: 写失败测试 `NativeBindingTests`**

```csharp
public unsafe class NativeBindingTests
{
    [Fact]
    public void GetVersion_Is4x()
    {
        string? version = Marshal.PtrToStringUTF8((IntPtr)openslide.get_version());
        Assert.NotNull(version);
        Assert.StartsWith("4.", version);
    }

    [Fact]
    public void PropertyNameConstants_AreNulTerminated()
    {
        ReadOnlySpan<byte> vendor = openslide.OPENSLIDE_PROPERTY_NAME_VENDOR;
        Assert.Equal(0, vendor[^1]);
        Assert.Equal("openslide.vendor", Encoding.UTF8.GetString(vendor[..^1]));
    }
}
```

- [ ] **Step 6: 运行测试**

Run: `pwsh eng/Get-OpenSlide.ps1; dotnet test OpenSlideSharp.slnx -c Release`
Expected: 2 passed。

- [ ] **Step 7: Commit**

```bash
git add -A eng src test OpenSlideSharp.slnx && git commit -m "feat: generate OpenSlide 4.x bindings with ClangSharp"
```

---

### Task 4: 原生库加载

**Files:**
- Create: `src/OpenSlideSharp/OpenSlideLibrary.cs`、`src/OpenSlideSharp/Native/NativeLibraryLoader.cs`
- Test: `test/OpenSlideSharp.Tests/NativeLibraryLoaderTests.cs`

**Interfaces:**
- Produces: `public static class OpenSlideLibrary`
  - `public const string LibraryName = "libopenslide-1";`
  - `public const string LibraryPathEnvironmentVariable = "OPENSLIDE_LIBRARY_PATH";`
  - `public static string Version { get; }`：返回 `openslide_get_version()`。
  - `#if NET` `public static void SetLibraryPath(string path)`：库已解析后再调用抛 `InvalidOperationException`。
- Produces: `internal enum NativeOS { Windows, Linux, MacOS }`
- Produces: `internal static class NativeLibraryLoader`
  - `IReadOnlyList<string> GetFallbackNames(NativeOS os)`
  - `string MissingLibraryMessage`
  - `#if NET`：用 `[ModuleInitializer]` 注册 `NativeLibrary.SetDllImportResolver`。
  - `#if NET` `bool IsResolved`
- 解析顺序：`SetLibraryPath` → 环境变量 → `NativeLibrary.TryLoad(LibraryName, assembly, searchPath)` → `GetFallbackNames(当前 OS)`。
  - 显式路径（API 或环境变量）加载失败时，直接抛 `DllNotFoundException` 并带上该路径，不回退到后面的步骤。
  - 全部失败返回 `IntPtr.Zero`，交给运行时抛出异常；高层再把它改写成 `MissingLibraryMessage`（见 Task 5）。

- [ ] **Step 1: 写失败测试**

```csharp
public class NativeLibraryLoaderTests
{
    [Fact]
    public void FallbackNames_PerOs()
    {
        Assert.Empty(NativeLibraryLoader.GetFallbackNames(NativeOS.Windows));
        Assert.Equal(new[] { "libopenslide.so.1" }, NativeLibraryLoader.GetFallbackNames(NativeOS.Linux));
        Assert.Equal(new[] { "libopenslide.1.dylib", "/opt/homebrew/lib/libopenslide.1.dylib", "/usr/local/lib/libopenslide.1.dylib" },
                     NativeLibraryLoader.GetFallbackNames(NativeOS.MacOS));
    }

    [Fact]
    public void MissingLibraryMessage_ExplainsFix()
    {
        Assert.Contains("OpenSlideSharp.runtime", NativeLibraryLoader.MissingLibraryMessage);
        Assert.Contains("OPENSLIDE_LIBRARY_PATH", NativeLibraryLoader.MissingLibraryMessage);
    }

    [Fact]
    public void SetLibraryPath_AfterLoad_Throws()
    {
        Assert.StartsWith("4.", OpenSlideLibrary.Version);
        Assert.Throws<InvalidOperationException>(() => OpenSlideLibrary.SetLibraryPath("whatever"));
    }
}
```

测试项目需要 `InternalsVisibleTo`：核心 csproj 加 `<InternalsVisibleTo Include="OpenSlideSharp.Tests" />`。

- [ ] **Step 2: 运行，确认失败**（编译错误：类型不存在）

Run: `dotnet test OpenSlideSharp.slnx -c Release --filter NativeLibraryLoaderTests`

- [ ] **Step 3: 实现上述接口**

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 全部通过；`dotnet build -c Release` 对三个 TFM 都无警告。

- [ ] **Step 5: Commit**

```bash
git add -A src test && git commit -m "feat: resolve libopenslide across platforms with explicit override"
```

---

### Task 5: `OpenSlide` 打开与层级元数据

**Files:**
- Create: `src/OpenSlideSharp/OpenSlide.cs`、`src/OpenSlideSharp/OpenSlideException.cs`、`src/OpenSlideSharp/SlideSize.cs`、`src/OpenSlideSharp/Internal/OpenSlideHandle.cs`、`src/OpenSlideSharp/Internal/Utf8.cs`
- Test: `test/OpenSlideSharp.Tests/OpenSlideTests.cs`

**Interfaces:**
- Consumes: `openslide.*`（Task 3）、`NativeLibraryLoader.MissingLibraryMessage`（Task 4）
- Produces（计划 2–4 依赖）:
  - `public readonly record struct SlideSize(long Width, long Height);`
  - `public class OpenSlideException : Exception`，`public class OpenSlideUnsupportedFormatException : OpenSlideException`；两者都提供 `(string message)` 和 `(string message, Exception inner)` 构造函数。
  - `public sealed class OpenSlide : IDisposable`，本任务实现以下成员：
    - `static string? DetectFormat(string filename)`、`static OpenSlide Open(string filename)`
    - `string FileName`、`int LevelCount`、`SlideSize Dimensions`
    - `IReadOnlyList<SlideSize> LevelDimensions`、`IReadOnlyList<double> LevelDownsamples`
    - `int GetBestLevelForDownsample(double downsample)`
    - `override string ToString()`：返回 `$"OpenSlide({FileName})"`
    - `void Dispose()`：可重复调用
  - `internal sealed class OpenSlideHandle : SafeHandle`：`ReleaseHandle` 调用 `openslide.close`。
  - `internal T OpenSlide.Invoke<T>(Func<IntPtr, T> call)` 与 `void Invoke(Action<IntPtr>)`：
    1. 先 `DangerousAddRef`，已释放时抛 `ObjectDisposedException`；
    2. 检查 `get_error`，若已有错误抛 `OpenSlideException`；
    3. 执行调用；
    4. 再检查一次 `get_error`；
    5. 最后 `DangerousRelease`。
    - 后续所有成员都通过它访问原生句柄。
  - `internal static unsafe class Utf8`：`byte[] ToNullTerminated(string)`、`string? FromNative(byte*)`。
- 行为约定：
  - `Open` 收到 NULL 时抛 `OpenSlideUnsupportedFormatException`；句柄处于错误状态时先关闭句柄，再抛 `OpenSlideException(错误文本)`。
  - `filename` 为 null 时抛 `ArgumentNullException`。
  - `DetectFormat`、`Open`、`OpenSlideLibrary.Version` 捕获 `DllNotFoundException`，改为抛出 `new DllNotFoundException(MissingLibraryMessage, ex)`。
  - 层级元数据在打开时读取一次并缓存；每次访问仍要经过“已释放 / 粘滞错误”检查，以对齐 Python 每次都查询的语义。

- [ ] **Step 1: 写失败测试**（对应 openslide-python 的 `TestSlideWithoutOpening` / `TestSlide`）

```csharp
public class OpenSlideTests
{
    [Fact] public void DetectFormat_Missing_Null() => Assert.Null(OpenSlide.DetectFormat(TestFiles.Get("__missing_file")));
    [Fact] public void DetectFormat_NotASlide_Null() => Assert.Null(OpenSlide.DetectFormat(TestFiles.Get("README.md")));
    [Fact] public void DetectFormat_Boxes() => Assert.Equal("generic-tiff", OpenSlide.DetectFormat(TestFiles.Get("boxes.tiff")));

    [Fact]
    public void Open_Unsupported_Throws()
    {
        Assert.Throws<OpenSlideUnsupportedFormatException>(() => OpenSlide.Open("__does_not_exist"));
        Assert.Throws<OpenSlideUnsupportedFormatException>(() => OpenSlide.Open(TestFiles.Get("README.md")));
        Assert.Throws<OpenSlideUnsupportedFormatException>(() => OpenSlide.Open(TestFiles.Get("unopenable.tiff")));
        Assert.Throws<ArgumentNullException>(() => OpenSlide.Open(null!));
    }

    [Fact]
    public void Open_UnicodePath()
    {
        string path = TestFiles.Get("😐.svs");
        Assert.Equal("aperio", OpenSlide.DetectFormat(path));
        using var osr = OpenSlide.Open(path);
        Assert.Equal(new SlideSize(16, 16), osr.Dimensions);
    }

    [Fact] // Review Focus 2
    public void Open_ChineseDirectoryAndFileName_Works()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openslide 测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "切片.svs");
            File.Copy(TestFiles.Get("small.svs"), path);
            Assert.Equal("aperio", OpenSlide.DetectFormat(path));
            using var osr = OpenSlide.Open(path);
            using var reference = OpenSlide.Open(TestFiles.Get("small.svs"));
            Assert.Equal(reference.Dimensions, osr.Dimensions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BasicMetadata_Boxes()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(4, osr.LevelCount);
        Assert.Equal(new[] { new SlideSize(300, 250), new SlideSize(150, 125), new SlideSize(75, 62), new SlideSize(37, 31) }, osr.LevelDimensions);
        Assert.Equal(new SlideSize(300, 250), osr.Dimensions);
        Assert.Equal(4, osr.LevelDownsamples.Count);
        Assert.Equal(1.0, osr.LevelDownsamples[0]);
        Assert.Equal(2.0, osr.LevelDownsamples[1]);
        Assert.Equal(4.0, osr.LevelDownsamples[2], 0);
        Assert.Equal(8.0, osr.LevelDownsamples[3], 0);
        Assert.Equal(0, osr.GetBestLevelForDownsample(0.5));
        Assert.Equal(1, osr.GetBestLevelForDownsample(3));
        Assert.Equal(3, osr.GetBestLevelForDownsample(37));
        Assert.Equal($"OpenSlide({TestFiles.Get("boxes.tiff")})", osr.ToString());
    }

    [Fact]
    public void AfterDispose_MembersThrow_DisposeIdempotent()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(4, osr.LevelCount);
        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => osr.LevelCount);
        osr.Dispose();
    }
}
```

`Fixtures/README.md` 就是测试里用的“不是切片的文件”。

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test OpenSlideSharp.slnx -c Release --filter OpenSlideTests`
Expected: 编译失败（`OpenSlide` 未定义）。

- [ ] **Step 3: 实现上述接口**

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 全部通过。

- [ ] **Step 5: Commit**

```bash
git add -A src test && git commit -m "feat: add OpenSlide class with open, detection and level metadata"
```

---

### Task 6: 属性映射与错误语义

**Files:**
- Create: `src/OpenSlideSharp/OpenSlideProperties.cs`、`src/OpenSlideSharp/Internal/PropertyMap.cs`
- Modify: `src/OpenSlideSharp/OpenSlide.cs`（加 `Properties`）
- Test: `test/OpenSlideSharp.Tests/PropertyTests.cs`、`test/OpenSlideSharp.Tests/ErrorStateTests.cs`

**Interfaces:**
- Produces: `public static class OpenSlideProperties`，包含 13 个 `const string`：`BackgroundColor = "openslide.background-color"`、`Barcode = "openslide.barcode"`、`BoundsHeight`、`BoundsWidth`、`BoundsX`、`BoundsY`、`Comment`、`IccSize`、`MppX`、`MppY`、`ObjectivePower`、`Quickhash1 = "openslide.quickhash-1"`、`Vendor = "openslide.vendor"`。
- Produces: `public IReadOnlyDictionary<string, string> OpenSlide.Properties { get; }`，返回“活”的映射：
  - 每次查找调用 `get_property_value`，枚举调用 `get_property_names`，都经过 `Invoke`。
  - 缺失的键：索引器抛 `KeyNotFoundException`，`TryGetValue` 返回 false。
  - 切片释放后再访问，抛 `ObjectDisposedException`。

- [ ] **Step 1: 写失败测试**

```csharp
public class PropertyTests
{
    [Fact]
    public void Properties_Boxes()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal("generic-tiff", osr.Properties[OpenSlideProperties.Vendor]);
        Assert.Throws<KeyNotFoundException>(() => osr.Properties["__does_not_exist"]);
        Assert.False(osr.Properties.TryGetValue("__does_not_exist", out _));
        Assert.Equal(osr.Properties.Count, osr.Properties.Count());
        Assert.True(osr.Properties.ContainsKey(OpenSlideProperties.Vendor));
    }

    [Fact] // 手写常量必须存在于生成层且取值一致（单向：允许生成层多出 nightly 新常量）
    public void PropertyConstants_MatchNativeHeader()
    {
        var native = typeof(openslide).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.Name.StartsWith("OPENSLIDE_PROPERTY_NAME_"))
            .Select(p => NativeConstant(p)).ToHashSet();
        foreach (var f in typeof(OpenSlideProperties).GetFields(BindingFlags.Public | BindingFlags.Static))
            Assert.Contains((string)f.GetRawConstantValue()!, native);
    }

    [Fact]
    public void Properties_AfterDispose_Throws()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var props = osr.Properties;
        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => props[OpenSlideProperties.Vendor]);
    }
}

public class ErrorStateTests
{
    [Fact]
    public void ReadBadRegion_IsSticky()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("unreadable.svs"));
        Assert.Equal("aperio", osr.Properties[OpenSlideProperties.Vendor]);
        Assert.Throws<OpenSlideException>(() => osr.ReadRegion(0, 0, 0, 16, 16));
        Assert.Throws<OpenSlideException>(() => osr.Properties[OpenSlideProperties.Vendor]);
    }

    [Fact] // Review Focus 5
    public void Dispose_AfterStickyError_DoesNotThrow()
    {
        var osr = OpenSlide.Open(TestFiles.Get("unreadable.svs"));
        Assert.Throws<OpenSlideException>(() => osr.ReadRegion(0, 0, 0, 16, 16));
        osr.Dispose();
        osr.Dispose();
    }
}
```

`NativeConstant(PropertyInfo p)`：通过反射取得 `ReadOnlySpan<byte>` 属性的 getter，用 `CreateDelegate` 构造出返回 `ReadOnlySpan<byte>` 的委托，调用后去掉末尾的 NUL，再按 UTF-8 解码。`ReadOnlySpan` 不能装箱，所以不能直接用 `GetValue`。

`ErrorStateTests` 里用到的 `ReadRegion` 在 Task 7 才实现。因此本任务先只让 `PropertyTests` 通过；`ErrorStateTests` 写好后加上 `Skip = "needs ReadRegion (Task 7)"`，Task 7 再去掉 Skip。

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test OpenSlideSharp.slnx -c Release --filter PropertyTests`

- [ ] **Step 3: 实现 `OpenSlideProperties` 和 `PropertyMap`，并接入 `OpenSlide.Properties`**

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 全部通过，`ErrorStateTests` 两个用例显示为 skipped。

- [ ] **Step 5: Commit**

```bash
git add -A src test && git commit -m "feat: expose live slide properties with OpenSlide error semantics"
```

---

### Task 7: 区域读取、ICC、像素图与缓存

**Files:**
- Create: `src/OpenSlideSharp/PixelImage.cs`、`src/OpenSlideSharp/PixelFormat.cs`、`src/OpenSlideSharp/OpenSlideCache.cs`、`src/OpenSlideSharp/Internal/CacheHandle.cs`
- Modify: `src/OpenSlideSharp/OpenSlide.cs`（加 `ReadRegion`、`ColorProfile`、`SetCache`）
- Test: `test/OpenSlideSharp.Tests/ReadRegionTests.cs`、`test/OpenSlideSharp.Tests/PixelImageTests.cs`、`test/OpenSlideSharp.Tests/CacheTests.cs`；去掉 `ErrorStateTests` 上的 Skip

**Interfaces:**
- Produces（计划 2 依赖）:
  - `public enum PixelFormat { Bgra32Premultiplied, Bgra32, Bgra32Opaque }`：内存字节顺序 B、G、R、A，与 OpenSlide 在小端机上的预乘 ARGB 一致。
  - `public sealed class PixelImage`
    - 构造：`PixelImage(int width, int height, PixelFormat format, byte[] data, byte[]? iccProfile = null)`，要求 `data.Length == width * height * 4`，否则抛 `ArgumentException`。
    - 成员：`int Width`、`int Height`、`PixelFormat Format`、`byte[] Data`、`byte[]? IccProfile`、`int Stride => Width * 4`。
    - `PixelImage ToStraightAlpha()`：`Bgra32Premultiplied` 转成 `Bgra32`，其他格式原样返回同一实例。
  - `public byte[]? OpenSlide.ColorProfile { get; }`：大小为 0 时返回 null；首次读取后缓存。
  - `public void OpenSlide.ReadRegion(long x, long y, int level, int width, int height, Span<byte> destination)`：写入预乘 BGRA。
    - `width` 或 `height` 小于 0：抛 `ArgumentOutOfRangeException`。
    - `destination.Length < width * height * 4`（用 long 计算）：抛 `ArgumentException`。
  - `public PixelImage OpenSlide.ReadRegion(long x, long y, int level, int width, int height)`：`Format = Bgra32Premultiplied`，`IccProfile = ColorProfile`；`width * height * 4 > Array.MaxLength`（`netstandard` 上为 `0x7FFFFFC7`）时抛 `ArgumentOutOfRangeException`。
  - `public sealed class OpenSlideCache : IDisposable`：构造 `OpenSlideCache(long capacityBytes)`，`capacityBytes < 0` 抛 `ArgumentOutOfRangeException`。
  - `public void OpenSlide.SetCache(OpenSlideCache cache)`：传 null 抛 `ArgumentNullException`。OpenSlide 内部对缓存做引用计数，所以设置之后即使先释放 cache，切片也仍然可用。
- 反预乘算法对齐 openslide-python 的 `_convert.c`：`a == 255` 时原样；`a == 0` 时 RGB 都置 0；否则每个通道为 `c = 255 * c / a`，整数除法向下截断。

- [ ] **Step 1: 写失败测试**

```csharp
public class ReadRegionTests
{
    [Fact]
    public void ReadRegion_Sizes()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var img = osr.ReadRegion(-10, -10, 1, 400, 400);
        Assert.Equal((400, 400), (img.Width, img.Height));
        Assert.Equal(400 * 400 * 4, img.Data.Length);
        Assert.Equal(PixelFormat.Bgra32Premultiplied, img.Format);
        Assert.Equal((400, 0), (osr.ReadRegion(0, 0, 1, 400, 0).Width, osr.ReadRegion(0, 0, 1, 400, 0).Height));
        Assert.Equal(100, osr.ReadRegion(0, 0, 4, 100, 100).Width); // 越界 level 不报错，同 Python
        Assert.Throws<ArgumentOutOfRangeException>(() => osr.ReadRegion(0, 0, 1, 400, -5));
    }

    [Fact] // Review Focus 3
    public void ReadRegion_TooLargeForArray_Throws()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Throws<ArgumentOutOfRangeException>(() => osr.ReadRegion(0, 0, 0, 65536, 65536));
    }

    [Fact] // Review Focus 3
    public void ReadRegion_SpanTooSmall_Throws()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Throws<ArgumentException>(() => osr.ReadRegion(0, 0, 0, 10, 10, new byte[10 * 10 * 4 - 1]));
    }

    [Fact]
    public void ColorProfile_Boxes_And_Svs()
    {
        using var boxes = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        Assert.Equal(588, boxes.ColorProfile!.Length);
        Assert.Equal("mntr", Encoding.ASCII.GetString(boxes.ColorProfile, 12, 4));
        Assert.Equal(588, boxes.ReadRegion(0, 0, 0, 100, 100).IccProfile!.Length);
        using var svs = OpenSlide.Open(TestFiles.Get("small.svs"));
        Assert.Null(svs.ColorProfile);
        Assert.Null(svs.ReadRegion(0, 0, 0, 100, 100).IccProfile);
    }

    [Fact] // Review Focus 1
    public void ReadRegion_ConcurrentWithDispose_OnlyObjectDisposed()
    {
        var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        var errors = new ConcurrentBag<Exception>();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                try { osr.ReadRegion(0, 0, 0, 300, 250); }
                catch (Exception e) { errors.Add(e); }
            }
        })).ToArray();
        Thread.Sleep(5);
        osr.Dispose();
        Task.WaitAll(readers);
        Assert.All(errors, e => Assert.IsType<ObjectDisposedException>(e));
    }
}

public class PixelImageTests
{
    [Fact]
    public void ToStraightAlpha_MatchesPythonConvert()
    {
        var src = new PixelImage(3, 1, PixelFormat.Bgra32Premultiplied,
            new byte[] { 16, 32, 64, 128,   10, 20, 30, 255,   5, 6, 7, 0 });
        var dst = src.ToStraightAlpha();
        Assert.Equal(PixelFormat.Bgra32, dst.Format);
        Assert.Equal(new byte[] { 31, 63, 127, 128,   10, 20, 30, 255,   0, 0, 0, 0 }, dst.Data);
    }

    [Fact]
    public void Constructor_RejectsWrongLength() =>
        Assert.Throws<ArgumentException>(() => new PixelImage(2, 2, PixelFormat.Bgra32, new byte[15]));
}

public class CacheTests
{
    [Fact]
    public void CreateCache()
    {
        new OpenSlideCache(0).Dispose();
        new OpenSlideCache(1).Dispose();
        new OpenSlideCache(4 << 20).Dispose();
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenSlideCache(-1));
    }

    [Fact]
    public void SetCache_ThenRead()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes.tiff"));
        using (var cache = new OpenSlideCache(64 << 10)) osr.SetCache(cache);
        Assert.Equal(400, osr.ReadRegion(0, 0, 0, 400, 400).Width);
        Assert.Throws<ArgumentNullException>(() => osr.SetCache(null!));
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 编译失败（`PixelImage`、`ReadRegion` 未定义）。

- [ ] **Step 3: 实现上述接口**

数组版 `ReadRegion` 要用 `fixed` 固定新分配的数组，再调用 `read_region`，避免多拷贝一次。

- [ ] **Step 4: 运行，确认全部通过**（包括刚去掉 Skip 的 `ErrorStateTests`）

Run: `dotnet test OpenSlideSharp.slnx -c Release`
Expected: 全部通过，没有 skipped。

- [ ] **Step 5: Commit**

```bash
git add -A src test && git commit -m "feat: read regions, ICC profiles and OpenSlide caches"
```

---

### Task 8: 关联图像

**Files:**
- Create: `src/OpenSlideSharp/AssociatedImage.cs`、`src/OpenSlideSharp/Internal/AssociatedImageMap.cs`
- Modify: `src/OpenSlideSharp/OpenSlide.cs`（加 `AssociatedImages`）
- Test: `test/OpenSlideSharp.Tests/AssociatedImageTests.cs`

**Interfaces:**
- Produces（计划 2–4 依赖）:
  - `public sealed class AssociatedImage`
    - `string Name`、`SlideSize Dimensions`
    - `byte[]? ColorProfile`：大小为 0 时返回 null，首次读取后缓存。
    - `PixelImage Read()`：`Bgra32Premultiplied`，`IccProfile = ColorProfile`。
    - `OpenSlide Slide`（internal）。
    - 所有原生调用都经过 `Slide.Invoke`。
  - `public IReadOnlyDictionary<string, AssociatedImage> OpenSlide.AssociatedImages { get; }`：“活”映射。
    - 名称列表在首次访问时读取并缓存，切片打开后名称不会变化。
    - 索引器和枚举每次都要先检查切片是否已释放。
    - 缺失的键抛 `KeyNotFoundException`。

- [ ] **Step 1: 写失败测试**

```csharp
public class AssociatedImageTests
{
    [Fact]
    public void Aperio_Thumbnail()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("small.svs"));
        var thumb = osr.AssociatedImages["thumbnail"];
        Assert.Equal(new SlideSize(16, 16), thumb.Dimensions);
        var img = thumb.Read();
        Assert.Equal((16, 16), (img.Width, img.Height));
        Assert.Null(thumb.ColorProfile);
        Assert.Null(img.IccProfile);
        Assert.Throws<KeyNotFoundException>(() => osr.AssociatedImages["__missing"]);
        Assert.Equal(osr.AssociatedImages.Count, osr.AssociatedImages.Count());
    }

    [Fact]
    public void Dicom_SharedColorProfile()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("boxes_0.dcm"));
        Assert.Equal(456, osr.ColorProfile!.Length);
        Assert.Equal("mntr", Encoding.ASCII.GetString(osr.ColorProfile, 12, 4));
        Assert.Equal(456, osr.ReadRegion(0, 0, 0, 100, 100).IccProfile!.Length);
        Assert.Equal(osr.ColorProfile, osr.AssociatedImages["thumbnail"].ColorProfile);
    }

    [Fact]
    public void UnreadableAssociated_IsSticky()
    {
        using var osr = OpenSlide.Open(TestFiles.Get("unreadable.svs"));
        Assert.Equal("aperio", osr.Properties[OpenSlideProperties.Vendor]);
        Assert.Throws<OpenSlideException>(() => osr.AssociatedImages["thumbnail"].Read());
        Assert.Throws<OpenSlideException>(() => osr.Properties[OpenSlideProperties.Vendor]);
    }

    [Fact]
    public void AssociatedImages_AfterDispose_Throw()
    {
        var osr = OpenSlide.Open(TestFiles.Get("small.svs"));
        var map = osr.AssociatedImages;
        osr.Dispose();
        Assert.Throws<ObjectDisposedException>(() => map["thumbnail"]);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test OpenSlideSharp.slnx -c Release --filter AssociatedImageTests`

- [ ] **Step 3: 实现上述接口**

- [ ] **Step 4: 运行，确认全部通过**

Run: `dotnet test OpenSlideSharp.slnx -c Release`

- [ ] **Step 5: Commit**

```bash
git add -A src test && git commit -m "feat: expose associated images with ICC profiles"
```

---

### Task 9: `OpenSlideSharp.runtime`（.NET）与包冒烟测试

**Files:**
- Create: `src/OpenSlideSharp.runtime/OpenSlideSharp.runtime.csproj`、`src/OpenSlideSharp.runtime/README.md`、`src/OpenSlideSharp.runtime/build/net462/OpenSlideSharp.runtime.targets`、`src/OpenSlideSharp/README.md`（核心包的包内 README）
- Create: `test/PackageSmoke/PackageSmoke.csproj`、`test/PackageSmoke/Program.cs`、`test/PackageSmoke/Directory.Build.props`、`test/PackageSmoke/Directory.Packages.props`、`test/PackageSmoke/NuGet.config`
- Create: `eng/Test-Packages.ps1`

**Interfaces:**
- Consumes: `artifacts/native/` 布局（Task 2）、`OpenSlide`（Task 5–8）
- Produces: runtime 包内容（Task 10 在此基础上追加 `build/native/`）:

```
runtimes/win-x64/native/libopenslide-1.dll
runtimes/linux-x64/native/libopenslide-1.so
runtimes/linux-arm64/native/libopenslide-1.so
runtimes/osx-arm64/native/libopenslide-1.dylib
runtimes/osx-x64/native/libopenslide-1.dylib
build/net462/OpenSlideSharp.runtime.targets
buildTransitive/net462/OpenSlideSharp.runtime.targets   （与 build/ 下的同一个源文件）
licenses/**   VERSIONS.md   versions.json   README.md   openslide_logo.png
```

- runtime csproj 的设置：
  - `TargetFramework=netstandard2.0`、`IncludeBuildOutput=false`、`SuppressDependenciesWhenPacking=true`、`NoWarn=NU5128`；
  - `PackageLicenseExpression=LGPL-2.1-only`；
  - `PackageTags=openslide wsi native`；
  - Description 写明 “Native OpenSlide 4.x libraries from openslide-bin (https://github.com/openslide/openslide-bin); source: openslide-bin source tarball of the same version.”；
  - 文件都用 `None Pack=true PackagePath=…` 加入；`artifacts/native` 缺少任何一个 RID 目录时报错。
- `net462` targets：只在 `'$(TargetFrameworkIdentifier)' == '.NETFramework' and '$(RuntimeIdentifier)' == ''` 时，把 win-x64 的 DLL 作为 `None`（`CopyToOutputDirectory=PreserveNewest`）复制到输出目录。
- `PackageSmoke` 冒烟项目：
  - 用空的 `Directory.Build.props`，并在 `Directory.Packages.props` 里设 `ManagePackageVersionsCentrally=false`，从而与仓库的公共设置隔离。
  - 目标框架：`net10.0`；Windows 上为 `net10.0;net48`，`PlatformTarget=x64`。
  - 引用 `OpenSlideSharp` 和 `OpenSlideSharp.runtime`，版本均为 `$(OpenSlideSharpVersion)`。`-p:WithoutRuntime=true` 时不引用 runtime 包。
  - `NuGet.config` 的源指向 `../../artifacts/smoke-feed`。
  - `Program`：
    - 打开 `args[0]`，输出 `version=<OpenSlideLibrary.Version>`、`vendor=<Properties[Vendor]>`、`size=<W>x<H>`，退出码 0；
    - 捕获到 `DllNotFoundException` 时输出其 Message，退出码 3。
- `eng/Test-Packages.ps1 [-Version 0.0.0-smoke.<时间戳>]`，执行顺序：
  1. 用 `dotnet pack` 打出核心包和 runtime 包，输出到 `artifacts/smoke-feed`；
  2. 列出 runtime nupkg 的条目，断言上面列出的文件都在，且没有 `.debug`；
  3. 设置 `$env:NUGET_PACKAGES = artifacts/smoke-packages`，隔离全局包缓存。然后对每个 TFM：
     - 执行 `dotnet build test/PackageSmoke -c Release -f <tfm> --artifacts-path artifacts/smoke/<变体> -p:OpenSlideSharpVersion=$v`；
     - 运行构建产物并传入 `small.svs`：net10.0 用 `dotnet PackageSmoke.dll`，net48 直接运行 `PackageSmoke.exe`；
     - 断言退出码为 0，输出含 `vendor=aperio`；
  4. 对 no-runtime 变体（只跑 `net10.0`）断言退出码为 3，输出含 `OpenSlideSharp.runtime`。
  - 每次运行前清空 `artifacts/smoke*`，避免 NuGet 全局缓存或旧输出干扰结果。

- [ ] **Step 1: 先写冒烟项目和 `Test-Packages.ps1`，运行确认失败**

Run: `pwsh eng/Test-Packages.ps1`
Expected: 失败，因为 `OpenSlideSharp.runtime` 项目还不存在。

- [ ] **Step 2: 实现 runtime csproj、targets 和两个包内 README**，并 `dotnet sln add` runtime 项目

- [ ] **Step 3: 下载全部平台并运行冒烟**

Run: `pwsh eng/Get-OpenSlide.ps1 -Platform all; pwsh eng/Test-Packages.ps1`
Expected: Windows 上 `net10.0`、`net48` 与 no-runtime 三个变体全部 PASS，脚本退出码为 0。

- [ ] **Step 4: Commit**

```bash
git add -A src eng test OpenSlideSharp.slnx && git commit -m "feat: add OpenSlideSharp.runtime package with smoke tests"
```

---

### Task 10: runtime 包的 VS C++ 支持

**Files:**
- Create: `src/OpenSlideSharp.runtime/build/native/OpenSlideSharp.runtime.targets`
- Modify: `src/OpenSlideSharp.runtime/OpenSlideSharp.runtime.csproj`（打包 `build/native/include/openslide/*.h`、`build/native/lib/x64/libopenslide.lib`）
- Create: `test/NativeCppSmoke/NativeCppSmoke.vcxproj`、`test/NativeCppSmoke/main.c`
- Modify: `eng/Test-Packages.ps1`（追加 C++ 阶段）

**Interfaces:**
- Consumes: Task 9 的包内容；`artifacts/native/msvc/x64/libopenslide.lib` 与 `include/`（Task 2）
- Produces: `build/native/OpenSlideSharp.runtime.targets`
  - `$(Platform)` 为 `x64` 时：
    - `ClCompile.AdditionalIncludeDirectories` 加入 `$(MSBuildThisFileDirectory)include` 和 `…include\openslide`，使 `<openslide.h>` 和 `<openslide/openslide.h>` 两种写法都能用；
    - `Link.AdditionalLibraryDirectories` 加入 `…lib\x64`；
    - `Link.AdditionalDependencies` 加入 `libopenslide.lib`；
    - 增加 target `OpenSlideCopyNative`（`AfterTargets="Build"`），把 `$(MSBuildThisFileDirectory)..\..\runtimes\win-x64\native\libopenslide-1.dll` 复制到 `$(OutDir)`。
  - 其他平台：target `OpenSlideCheckPlatform`（`BeforeTargets="PrepareForBuild"`）报错，错误文本为 `OpenSlideSharp.runtime: OpenSlide provides Windows binaries for x64 only; set Platform to x64 (current: $(Platform)).`
- `NativeCppSmoke.vcxproj`：
  - 控制台程序，`PlatformToolset=$(DefaultPlatformToolset)`，配置 `Release|x64` 和 `Release|Win32`；
  - 采用 packages.config 方式，导入 `$(MSBuildThisFileDirectory)packages\OpenSlideSharp.runtime.$(OpenSlideRuntimeVersion)\build\native\OpenSlideSharp.runtime.targets`；`packages.config` 由脚本按版本号生成，加入 `.gitignore`。
- `main.c`：
  - 打印 `version=%s`（来自 `openslide_get_version()`）；
  - 用 `openslide_open(argv[1])` 打开切片，失败或 `openslide_get_error` 非空时返回 2；
  - 打印 `size=%lldx%lld`（来自 `openslide_get_level0_dimensions`），返回 0。
- `Test-Packages.ps1` 的 C++ 阶段（仅 Windows）：
  1. 若 `artifacts/tools/nuget.exe` 不存在，从 `https://dist.nuget.org/win-x86-commandline/latest/nuget.exe` 下载；
  2. 执行 `nuget restore test/NativeCppSmoke/packages.config -PackagesDirectory test/NativeCppSmoke/packages -Source artifacts/smoke-feed`；
  3. 用 vswhere 找到 MSBuild，以 `/p:Configuration=Release /p:Platform=x64 /p:OpenSlideRuntimeVersion=$v` 构建；
  4. 运行生成的 exe 并传入 `small.svs`，断言输出含 `version=4.`，退出码为 0；
  5. 再以 `/p:Platform=Win32` 构建，断言失败，且输出含 `Windows binaries for x64 only`。
  - 同时补充包内容断言：`build/native/OpenSlideSharp.runtime.targets`、`build/native/include/openslide/openslide.h`、`build/native/lib/x64/libopenslide.lib` 都必须存在。
  - 若 MSBuild 报 “此时不应有 \Windows。”，按维护者全局说明的步骤检查系统 PATH 中是否有多余的双引号。

- [ ] **Step 1: 先写 vcxproj、main.c 和脚本的 C++ 阶段，运行确认失败**

Run: `pwsh eng/Test-Packages.ps1`
Expected: C++ 阶段失败，原因是包内缺少 `build/native` 下的 targets。

- [ ] **Step 2: 实现 `build/native` 的 targets，并改 csproj 打包头文件和导入库**

- [ ] **Step 3: 运行，确认通过**

Run: `pwsh eng/Test-Packages.ps1`
Expected:
- .NET 的三个变体与 C++ x64 全部 PASS；
- Win32 按预期失败，输出含 `Windows binaries for x64 only`；
- 脚本退出码为 0。

- [ ] **Step 4: Commit**

```bash
git add -A src eng test .gitignore && git commit -m "feat: support Visual Studio C++ projects in OpenSlideSharp.runtime"
```

---

### Task 11: 基础 CI

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `eng/Get-OpenSlide.ps1`、`eng/Generate-Bindings.ps1`、`eng/Test-Packages.ps1`
- Produces: 计划 5 会在这个 workflow 的基础上扩展，包括 job 名称和产物名：
  - `test`：矩阵为 `windows-latest`、`ubuntu-latest`、`ubuntu-24.04-arm`、`macos-latest`。步骤依次为 checkout、setup-dotnet（读取 `global.json`）、`pwsh eng/Get-OpenSlide.ps1`、`dotnet test OpenSlideSharp.slnx -c Release`。
  - `bindings`：运行在 `windows-latest`。执行 `pwsh eng/Generate-Bindings.ps1` 后 `git diff --exit-code src/OpenSlideSharp/Native`，有差异即失败，提示“生成文件与清单不一致”。
  - `package`：运行在 `windows-latest`，依赖 `test`。执行 `Get-OpenSlide.ps1 -Platform all`，再执行 `Test-Packages.ps1 -Version 0.0.0-ci.${{ github.run_number }}`。上传 `artifacts/smoke-feed/*.nupkg`，产物名为 `packages`。
  - `package-unix`：依赖 `package`，矩阵为 ubuntu / arm / macos。下载 `packages` 产物后执行 `Test-Packages.ps1 -SkipPack -Version 0.0.0-ci.${{ github.run_number }}`，只跑 net10.0 的冒烟。为此 Task 9 的脚本需要支持 `-SkipPack` 参数：跳过打包，直接使用已有的源。
- 触发条件：`push`（全部分支）、`pull_request`、`workflow_dispatch`；`permissions: contents: read`。actions 使用各自最新的主版本标签。

- [ ] **Step 1: 给 `Test-Packages.ps1` 增加 `-SkipPack` 参数，并在本地验证**

Run: `pwsh eng/Test-Packages.ps1 -SkipPack -Version <上一次冒烟用的版本号>`
Expected: 不重新打包，冒烟全部 PASS。

- [ ] **Step 2: 写 `ci.yml`**

- [ ] **Step 3: 本地做语法检查**

Run: 如果装了 `actionlint` 就用它检查；否则执行 `python -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml', encoding='utf-8'))"`（缺 PyYAML 时先 `pip install pyyaml`）。
Expected: 无解析错误。

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml eng/Test-Packages.ps1 && git commit -m "ci: build and test on four platforms, verify packages"
```

- [ ] **Step 5: 推送分支，观察 CI 结果。推送前必须先征得维护者同意**

Run（获准后执行）: `git push -u origin refactor/openslide-4x`，然后 `gh run watch`
Expected: `test`（4 个平台）、`bindings`、`package`、`package-unix` 全部成功。如果 Linux 或 macOS 失败，逐一排查并修复。
