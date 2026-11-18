# Plan 5：上游同步与发布 Implementation Plan

**Goal:** 让仓库自动跟踪 openslide.org/builds 上的 openslide-bin 正式版与 nightly。上游更新时按以下顺序处理：

1. 重新生成绑定；
2. 在四平台上测试；
3. 提交到 master；
4. 按规格 §7.2 计算版本号；
5. 把 5 个 NuGet 包推送到 nuget.org（维护者设置 `NUGET_API_KEY` 之后才会推送，未设置时只做演练）；
6. 创建 GitHub Release，附上 nupkg、查看器的三平台构建和 openslide-bin 源码包。

同时清掉计划 1–4 遗留的所有“发布前必须处理”事项。

**Architecture:**
- 发布逻辑分为三层：
  - `eng/` 中的 PowerShell 脚本：版本计算、上游检测、清单更新、打包，每个脚本都带 `-SelfTest`；
  - 三个 workflow：`ci.yml` 改为可复用；新增 `release.yml`、`upstream-sync.yml`；
  - 仓库文档。
- 发布必须先跑完 CI 的四平台矩阵（复用 `ci.yml`），通过后才执行。
- 没有 `NUGET_API_KEY` 时整个发布降级为演练：只构建、打包并上传 workflow artifact，不打 tag、不建 Release、不推送。

**Tech Stack:** GitHub Actions（action 主版本与现有 ci.yml 一致）、PowerShell 7、.NET 10 SDK、nuget.org v3 API、GitHub REST API（`gh`）。

**Spec:** `docs/design/2026-11-17-openslidesharp-v4-refactor-design.md`（§3、§7、§9）。遗留事项：`docs/notes/2026-11-17-plan1-carryover.md` 至 `docs/notes/2026-11-18-plan4-carryover.md` 中 “发布前必须处理” 各节。

## Global Constraints

- **版本号**（§7.2）。所有包共用同一版本号：

  | 情况 | NuGet 版本 |
  |---|---|
  | 正式版 openslide-bin `X.Y.Z.B` | `X.Y.Z.B` |
  | nightly `X.Y.Z.B+YYYYMMDD.nightly`，openslide 版本 `…-g<hash8>` | `X.Y.Z.(B+1)-nightly.YYYYMMDD.g<hash8>` |
  | 上游未变、只改 C# 的第 N 次发布 | 上面的版本后加 `.N`（N 从 1 开始，按已有 tag 计算） |
  | 清单为正式版时只改 C# | 按 nightly 规则编号：日期取该正式版的发布日期，hash 取该 tag 对应的 openslide commit |

- **单调不变式：** 推送前查询 nuget.org 上 `OpenSlideSharp` 的全部版本（含预发布）。计算出的版本按 NuGet 的排序规则不高于已有最高版本时，拒绝发布。当前 nuget.org 上的最高版本是 `1.1.2`。
- **同步规则**（§7.2）：
  - 只接受高于清单当前版本、也高于已发布最高版本的正式版；旧系列的维护版跳过。
  - nightly 算出的版本必须高于已发布最高版本，否则跳过。
  - 同一次同步按 NuGet 版本从低到高处理，正式版优先。
  - 清单永不降级。
- **程序集元数据：** `InformationalVersion` = `<NuGet 版本>+openslide.<openslideVersion>`，例如 `4.0.1.3-nightly.20261102.g174630bf+openslide.v4.0.1-49-g174630bf`。Release notes 中写明 openslide-bin 版本、openslide 版本和 commit。
- **提交身份：** workflow 中的自动提交和 tag 一律使用 `IOL0ol1 <1353839731@qq.com>`。
- **推送凭据：** 用 `secrets.NUGET_API_KEY`，`dotnet nuget push … --skip-duplicate`。secret 不存在或为空时进入演练模式，日志用 `::notice::` 说明。workflow 在任何情况下都不得打印 secret。
- **发布前置：** release 必须在同一 ref 上复用 `ci.yml` 并全部通过后才执行（反目标：不得发布未经四平台测试的包）。
- **GitHub Release 内容：**
  - 5 个 nupkg（不含 snupkg）；
  - 查看器 3 个归档：`OpenSlideSharp.Viewer-<version>-win-x64.zip`、`-linux-x64.tar.gz`、`-osx-arm64.tar.gz`。每个归档 = 发布目录（可执行文件 + `licenses/`）；
  - openslide-bin 源码包：`openslide-bin-<binVersion>.tar.gz`，原样转存，满足 LGPL 的源码提供义务；
  - nightly 版本标为 prerelease。
- **Action 版本：** 沿用 `actions/checkout@v7`、`actions/setup-dotnet@v6`、`actions/cache@v6`、`actions/upload-artifact@v7`、`actions/download-artifact@v8`。每个 job 写 `timeout-minutes`，权限按 job 最小化。
- **脚本：** PowerShell 7（`#requires -Version 7`）。所有网络响应都要校验 HTTP 状态和 sha256。

## Review Focus

1. **NuGet 版本排序。** `4.0.1.3-nightly.20261102.g174630bf` 必须低于 `4.0.1.3`、高于 `4.0.1.2`，带 `.N` 的版本按数字比较（`.10` 高于 `.9`），四段与三段混排时也要正确。版本比较出错会导致发布出 nuget.org 上收不回的错序版本。→ Task 3 的 `-SelfTest` 用例。
2. **演练模式不留痕。** 没有 key 时不得打 tag、不得建 Release、不得推送任何东西，workflow 仍为绿色并输出 notice。→ Task 4 的演练检查；合并后用 `workflow_dispatch` 实跑一次。
3. **上游有更新但新版本测试失败。** 不提交、不发布；按上游版本开 issue，同一版本不重复开；临时分支要清理。→ Task 5 的 `-SelfTest`（模拟），合并后再用 `workflow_dispatch` 做一次演练。
4. **工具输出目录为符号链接时误删链接目标的内容**（计划 3 遗留）。→ Task 2 `OutputSafetyTests` 新用例。
5. **供应链：** nuget.exe 和上游资源都要固定并校验哈希，清单中的 sha256 还要与 GitHub 资源的 `digest` 交叉校验。→ Task 1。

---

### Task 1: 上游清单、原生库与打包脚本的发布前加固

**Files:**
- Modify:
  - `eng/Update-OpenSlideManifest.ps1`
  - `eng/Get-OpenSlide.ps1`
  - `eng/Test-Packages.ps1`
  - `eng/openslide.json`（重新生成，包含 source 资源）
  - `src/OpenSlideSharp.runtime/OpenSlideSharp.runtime.csproj`
  - `src/OpenSlideSharp.runtime/README.md`

**Interfaces:**
- **清单**新增 `"source": { "url": "…/openslide-bin-<binVersion>.tar.gz", "sha256": "…" }`。
  - `Update-OpenSlideManifest.ps1` 记录每个资源时，都要与 GitHub release API 中该资源的 `digest`（形如 `sha256:<hex>`）比对，不一致时报错退出。
  - 源码包同样需要校验。
- **`Get-OpenSlide.ps1`：**
  - licenses 改为按平台分目录：`artifacts/native/licenses/<archive-key>/…`，每个平台目录里各有一份 `VERSIONS.md`，根目录不再放 `VERSIONS.md` 和 `versions.json`。这样即使只取某一平台，内容也是确定的。
  - 写出 `artifacts/native/openslide-bin.json`，内容为清单的 `channel`、`binVersion`、`openslideVersion`、`openslideCommit`、`date`、`source`。
  - 新增 `-Verify` 开关：检查 `.stamp` 中各平台的 sha 与清单一致、预期文件都存在，不一致则报错退出。打包前（Test-Packages 的 pack 分支，以及 release）必须调用。
- **runtime 包：**
  - `licenses/<archive-key>/**` 与 `openslide-bin.json` 打进包内；
  - 许可改用 `PackageLicenseFile` 指向包内的 `LICENSE.txt`（新建 `src/OpenSlideSharp.runtime/LICENSE.txt`），说明包内原生库来自 openslide-bin，按 LGPL-2.1 授权，各平台静态链接的组件及其许可见 `licenses/<platform>/`，源码见 `openslide-bin.json` 中的 `source`；
  - README 同步更新。
- **`Test-Packages.ps1`：**
  - nuget.exe 固定为一个明确版本，从 `https://dist.nuget.org/win-x86-commandline/v<版本>/nuget.exe` 下载。脚本里写死 sha256，下载后校验，不一致即失败。实现时下载一次，记录版本和 sha256。
  - 依赖组断言改为精确断言：TFM 名称集合完全相等，依赖集合完全相等，不再用 `-lt 3`。
  - runtime 包断言新的 licenses 布局、`openslide-bin.json` 和 `LICENSE.txt`，并断言 nuspec 中为 `<license type="file">LICENSE.txt</license>`。
  - pack 分支开始前调用 `Get-OpenSlide.ps1 -Platform all -Verify`。

- [ ] **Step 1：实现，然后重新生成清单**（仍为当前最新 nightly），在本机运行 `pwsh eng/Get-OpenSlide.ps1 -Platform all -Force` 与 `pwsh eng/Test-Packages.ps1`。

Expected：`All package smoke tests passed.`

- [ ] **Step 2：用 `dotnet test OpenSlideSharp.slnx -c Release` 跑全部测试后提交**

提交信息：`build: verify upstream assets against GitHub digests, ship per-platform notices and pin nuget.exe`

---

### Task 2: 包元数据、许可声明与发布前的 API 修整

**Files:**
- Modify:
  - `src/OpenSlideSharp.Tool/OpenSlideSharp.Tool.csproj`
  - `src/OpenSlideSharp.Tool/Export/ExportSupport.cs`
  - `test/OpenSlideSharp.Tool.Tests/OutputSafetyTests.cs`
  - `samples/OpenSlideSharp.Viewer/OpenSlideSharp.Viewer.csproj`
  - `samples/OpenSlideSharp.Viewer/THIRD-PARTY-NOTICES.txt`
  - `src/OpenSlideSharp/DeepZoomGenerator.cs`
  - `src/OpenSlideSharp/OpenSlide.cs`（GetThumbnail）
  - `src/OpenSlideSharp.SkiaSharp/SkiaColorTransform.cs`（文档）
  - `Directory.Build.props`
  - 相应测试
- Create: `src/OpenSlideSharp.Tool/LICENSE.txt`
- Delete: `samples/OpenSlideSharp.Viewer/SOURCE.md`（改为发布时生成）

**Interfaces:**
- **工具包：**
  - 改用 `PackageLicenseFile`（`LICENSE.txt`）：工具代码为 MIT；随包分发的 openslide-bin 为 LGPL-2.1，其组件许可见 `licenses/openslide-bin/<platform>/`；SkiaSharp 与 HarfBuzzSharp 见 `licenses/skiasharp/`；查看器资源见 `licenses/viewer/`。
  - openslide-bin 的许可证目录按 Task 1 的新布局复制。
- **工具输出安全**（计划 3 遗留）：`EnsureReplaceableDirectory` 遇到输出根本身是符号链接或 junction（`LinkTarget != null`）时，直接以退出码 1 拒绝，提示 “is a link”，不删除任何内容。
  - 新用例 `Output_IsSymlinkToMarkedDirectory_IsRefused`：链接目标里放一个 marker 和一个文件，运行后目标内容完好。没有创建链接的权限时用 `Assert.Skip` 跳过，Linux 和 macOS 上一定会运行。
- **查看器 licenses：**
  - `THIRD-PARTY-NOTICES.txt` 为 MicroCom.Runtime 和 Tmds.DBus.Protocol 补上 MIT 许可全文；
  - 发布时由 `openslide-bin.json`（来自 runtime 包或 `artifacts/native`）生成 `licenses/SOURCE.md`，写入确切的 openslide-bin 版本、源码包 URL 与 sha256，以及用 `OPENSLIDE_LIBRARY_PATH` 替换原生库的说明；
  - `Test-Packages` 中查看器发布的断言同步改为检查 SOURCE.md 含 `binVersion`。
- **InformationalVersion：**
  - `Directory.Build.props` 新增可选属性 `OpenSlideUpstreamVersion`（由发布流程传入）。设置后令 `InformationalVersion = $(Version)+openslide.$(OpenSlideUpstreamVersion)`，并关闭 SDK 自动追加 git hash（`IncludeSourceRevisionInInformationalVersion=false`）。
  - 未设置时行为不变。
  - 测试：构建时传入该属性，`OpenSlideLibrary` 所在程序集的 `AssemblyInformationalVersionAttribute` 应符合预期。写成 `test/OpenSlideSharp.Tests/AssemblyVersionTests.cs`，读取的是测试运行时的程序集，所以这里只断言不为空，且 `+` 前面等于 `Version`；端到端的值由 Task 4 发布流程的日志检查。
- **API 修整**（计划 2 遗留）：
  - `DeepZoomGenerator`：去掉 `tileSize + 2*overlap ≤ int.MaxValue/4` 的构造期限制，改为在 `GetTile` 中用 `CheckedPixelBytes` 检查（`ParamName` 为 `level`），从而接受 Python 能接受的参数；
  - `OpenSlide.GetThumbnail`：先检查所选层级的读取大小，再分配输出；超限时 `ParamName` 为 `maxWidth`/`maxHeight`（取超限的那一维）；
  - `SkiaColorTransform` 的文档注明缓存以 ICC 数组引用为键，调用方不得在之后修改该数组。
  - 以上各项都配测试。

- [ ] **Step 1：写失败的测试**（符号链接、DeepZoom 大 tileSize、GetThumbnail 参数名、InformationalVersion 格式）
- [ ] **Step 2：实现**
- [ ] **Step 3：运行全部测试和 `pwsh eng/Test-Packages.ps1`**
- [ ] **Step 4：提交**（可以分成几个 fix/build/docs 提交）

---

### Task 3: 版本计算与上游检测脚本

**Files:**
- Create:
  - `eng/Get-ReleaseVersion.ps1`
  - `eng/Find-UpstreamUpdate.ps1`
  - `eng/NuGetVersion.psm1`（NuGet 版本解析与比较，供两个脚本共用）
- Modify: `.github/workflows/ci.yml`（新增 `scripts` job，在 ubuntu-latest 上运行两个脚本的 `-SelfTest`）

**Interfaces:**
- `NuGetVersion.psm1`：
  - `ConvertTo-NuGetVersion [string]`：解析 1–4 段数字加可选的 `-prerelease` 和 `+metadata`；
  - `Compare-NuGetVersion a b`：返回 -1、0 或 1，按 NuGet 规则比较：
    1. 数字段逐段比较，缺省视为 0；
    2. 有预发布标签的版本低于没有的；
    3. 预发布标签按 `.` 分段：纯数字段按数值比较，其余段按字符串忽略大小写比较；数字段低于字母段；段数少者较低；
    4. `+` 后的元数据不参与比较。
- `Get-ReleaseVersion.ps1 [-Manifest eng/openslide.json] [-PublishedVersions <string[]>] [-ExistingTags <string[]>] [-SelfTest]`：
  - 输出 JSON：`version`、`prerelease`（bool）、`informationalVersion`、`tag`（`v<version>`）、`releaseName`、`notes`（Markdown，含 openslide-bin 版本、openslide 版本和 commit、源码包链接）；
  - 版本按 §7.2 计算；
  - 基础版本对应的 tag 已存在时，N 取已有 `.N` 的最大值 + 1，没有则为 1；
  - 违反单调不变式时以非零退出，并说明原因；
  - 省略 `-PublishedVersions` 时从 `https://api.nuget.org/v3-flatcontainer/openslidesharp/index.json` 获取（404 视为空）；省略 `-ExistingTags` 时用 `git tag --list 'v*'`。
- `Find-UpstreamUpdate.ps1 [-Manifest …] [-PublishedVersions …] [-SelfTest]`：
  - 查询 openslide-bin 最新正式版（`repos/openslide/openslide-bin/releases/latest`）和 openslide/builds 最新的 nightly prerelease；
  - 按同步规则输出要处理的候选列表（JSON 数组，按 NuGet 版本从低到高，正式版优先），每项含 `channel` 和 `tag`；
  - 没有更新时输出空数组。
  - GitHub API 用 `GITHUB_TOKEN`（若有）认证。
- 两个脚本的 `-SelfTest` 都用内置的模拟数据，不访问网络，至少覆盖以下用例：

| 用例 | 期望 |
|---|---|
| `Compare 4.0.1.3-nightly.20261102.g174630bf < 4.0.1.3` | -1 |
| `Compare 4.0.1.3-nightly.20261102.g174630bf > 4.0.1.2` | 1 |
| `Compare …g174630bf.10 > …g174630bf.9` | 1 |
| `Compare 1.1.2 < 4.0.1.2` | -1 |
| `Compare 4.0.1 == 4.0.1.0` | 0 |
| `Compare 1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-beta` | 有序 |
| nightly 清单 `4.0.1.2+20261102.nightly`、`v4.0.1-49-g174630bf`，无 tag，已发布 `1.1.2` | `4.0.1.3-nightly.20261102.g174630bf` |
| 同上，已有 tag `v4.0.1.3-nightly.20261102.g174630bf` | `4.0.1.3-nightly.20261102.g174630bf.1` |
| 同上，已有 `.1`、`.2` | `.3` |
| 正式版清单 `4.0.1.2`（date `2026-06-20`，commit `fe79a971`），无 tag | `4.0.1.2` |
| 同上，已有 tag `v4.0.1.2` | `4.0.1.3-nightly.20260620.gfe79a971.1` |
| 已发布 `4.0.1.3`、清单 nightly 基线 `4.0.1.2` | 拒绝（单调） |
| 上游正式版 `4.0.0.14`、清单 `4.0.1.2` | 跳过（旧系列） |
| 上游有新正式版 `4.0.2.0` 和新 nightly `4.0.2.0+…` | 先正式版，再 nightly |

- [ ] **Step 1：先写 `-SelfTest` 用例并运行，确认失败**
- [ ] **Step 2：实现**
- [ ] **Step 3：自测通过；用真实网络各运行一次，记录输出**（当前清单、nuget.org 与上游）
- [ ] **Step 4：加入 CI 的 `scripts` job，提交**

---

### Task 4: `release.yml`

**Files:**
- Create: `.github/workflows/release.yml`
- Modify:
  - `.github/workflows/ci.yml`：增加 `workflow_call`（输入 `ref`），被调用时各 job checkout 该 ref；
  - `eng/Test-Packages.ps1`（如需要）：新增 `-PackOnly` 开关，供发布时只打包、不跑冒烟。

**Interfaces:**
- **触发：**
  - `push` 到 master，且 `src/**`、`eng/openslide.json`、`Directory.*.props` 有变更；
  - `workflow_dispatch`，输入 `dry_run`（bool，默认 false）；
  - `workflow_call`，输入 `ref`，供同步流程调用。
- **并发：** `concurrency: release`，不取消进行中的运行。
- **jobs：**
  1. `ci`：`uses: ./.github/workflows/ci.yml`，传入 ref。
  2. `version`：needs ci，在 ubuntu 上运行。
     - checkout 时 `fetch-depth: 0`，并获取 tags；
     - 运行 `Get-ReleaseVersion.ps1`，输出 version、tag、prerelease 等；
     - 判定演练模式：输入 `dry_run`，或 `secrets.NUGET_API_KEY` 为空（通过 `env` 传给 step 再判断，不得打印）。
  3. `pack`：needs version，在 windows-latest 上运行。
     - `Get-OpenSlide -Platform all -Force -Verify`；
     - 用 `-Version <version>` 和 `-p:OpenSlideUpstreamVersion=<openslideVersion>` 打出 5 个包：可以运行 `Test-Packages.ps1 -PackOnly`，也可以直接按其顺序执行 `dotnet pack`；
     - 上传 artifact `nupkgs`。
  4. `viewer`：needs version，matrix 为 win-x64、linux-x64、osx-arm64，均在 ubuntu-latest 上交叉发布。
     - 用本地 feed 中的 runtime 包（下载 `nupkgs` artifact），`-p:UseOpenSlideRuntimePackage=true`；
     - win-x64 打 zip，其余打 tar.gz；
     - 上传 artifact。
  5. `publish`：needs pack、viewer，**非演练**时才执行，`permissions: contents: write`。
     - 下载 artifacts 和 openslide-bin 源码包，按清单 sha256 校验；
     - `dotnet nuget push *.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json --skip-duplicate`；
     - 以 `IOL0ol1 <1353839731@qq.com>` 身份为发布的 ref 打 annotated tag `v<version>` 并 push；
     - 用 `gh release create` 创建 Release：notes 用 `Get-ReleaseVersion` 输出的 notes，nightly 加 `--prerelease`，附件按 Global Constraints。
  6. `dry-run-summary`：演练时执行，把版本、包列表和附件列表写入 `$GITHUB_STEP_SUMMARY`。
- **安全：**
  - 只有 `publish` job 能接触 key；
  - `pull_request` 事件不会触发 release；
  - 工作流文件中不得有 `echo` 输出 secret。

- [ ] **Step 1：编写 workflow**，用 `actionlint` 检查（如果本机不可用，在 CI 中加一个 actionlint step：`rhysd/actionlint` 的官方 docker 用法，或下载固定版本的二进制并校验 sha256）。
- [ ] **Step 2：在本机模拟 pack 与 viewer 步骤**（运行对应的 PowerShell 命令），确认产物齐全：5 个 nupkg 和 3 个归档，归档内有 licenses 和 SOURCE.md。
- [ ] **Step 3：提交。** `workflow_dispatch` 只能运行默认分支上的 workflow，所以要在合并后验证：在 master 上以 `dry_run=true` 运行一次，确认绿色、没有 tag、没有 Release。

---

### Task 5: `upstream-sync.yml` 与文档

**Files:**
- Create: `.github/workflows/upstream-sync.yml`
- Modify:
  - `README.md`：Versioning 一节说明自动发布；新增 “Old packages” 一节，列出需要弃用的旧包；
  - `src/OpenSlideSharp.runtime/README.md`：注明 VS 2026 18.7+ 的原生 `PackageReference` 用法未经测试；
  - `samples/OpenSlideSharp.Viewer/README.md`：Release 下载说明，以及 macOS 上 `xattr -d com.apple.quarantine` 的处理方法。

**Interfaces:**
- **触发：** `schedule`（每天 03:17 UTC）和 `workflow_dispatch`（输入 `dry_run`）。
- **并发：** `concurrency: upstream-sync`。
- **jobs：**
  1. `detect`：运行 `Find-UpstreamUpdate.ps1`，输出候选列表；为空时结束。
  2. `update`：只处理第一个候选，一次同步只推进一个版本，下一个留给下次运行。
     - 新建分支 `sync/<tag>`；
     - `Update-OpenSlideManifest.ps1 -Channel <channel> -Tag <tag>`；
     - `Get-OpenSlide -Platform current`；
     - `Generate-Bindings.ps1`（在 windows-latest 上运行，与 ci 的 bindings job 一致）；
     - 以 IOL0ol1 身份提交，提交信息为 `build: track openslide-bin <binVersion>`，推送分支（`permissions: contents: write`）。
  3. `ci`：`uses: ./.github/workflows/ci.yml`，输入 `ref: sync/<tag>`。
  4. `merge`：needs ci，成功且非演练时执行。
     - 把 master 快进到 sync 分支；无法快进时（master 已前进）改为开 PR，交给维护者处理；
     - 删除 sync 分支。
  5. `release`：needs merge，`uses: ./.github/workflows/release.yml`，输入 `ref: master`，并通过 `secrets: inherit` 传入 secret。
  6. `report-failure`：在 `if: failure()` 时执行。
     - 按标题 `Upstream sync failed: openslide-bin <tag>` 查找处于 open 状态的 issue，不存在才新建（`permissions: issues: write`）；
     - 正文附上运行链接；
     - 删除 sync 分支。
- **演练：** 只执行 detect 和 update。update 不推送，只把生成的 diff 写进 step summary。

- [ ] **Step 1：编写 workflow 和文档**，用 actionlint 检查。
- [ ] **Step 2：提交。** 合并后在 master 上以 `dry_run=true` 运行一次同步。当前清单已是最新的 nightly 时，预期为 detect 输出空列表，运行结束。
