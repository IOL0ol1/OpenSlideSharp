# 计划 5 遗留事项（供以后使用）

来源：计划 5（上游同步与发布）各任务审阅、整体终审及其修复轮。下列事项都已评估为**不阻塞计划 5 合并**。终审中要求在合并前、以及在设置 `NUGET_API_KEY` 之前修复的项目，已在修复轮中处理：
- `deepzoom` 的 `-s`/`-e` 过大时，先检查再动旧输出；
- README 写明快进之后发布失败的情况，以及没有 key 时同步仍会更新 `master`；
- 查看器归档不再带 runtime 包描述 nupkg 布局的 `LICENSE.txt`；
- release notes 按通道措辞，正式版紧接 nightly 发布时注明 `Native` 层可能少了只有 nightly 才有的函数；
- 三个查看器归档各自在本平台上运行 `--check`；
- 每个 ref 一个发布并发组；
- 同步回退 PR 的说明、文档与注释等小项。

## 设置 `NUGET_API_KEY` 之前（维护者操作）

- **nuget.org 上的包 ID。** 预留 `OpenSlideSharp` ID 前缀，或先手动占住 4 个新 ID：`OpenSlideSharp.SkiaSharp`、`OpenSlideSharp.runtime`、`OpenSlideSharp.AspNetCore`、`OpenSlideSharp.Tool`。`OpenSlideSharp` 的 owner 是 `kiolp`。
- **创建 key。** 由 `kiolp` 账号创建，glob 为 `OpenSlideSharp*`，勾选 “Push new packages and package versions”，存为仓库 secret `NUGET_API_KEY`。
- **可以考虑 `environment: release`。** 把 key 放进一个部署分支只允许 `master` 的 environment，并给 release.yml 的 publish job 加上 `environment: release`。这样“只有 master 能拿到 key”由平台保证。
- **旧包弃用。** 在 nuget.org 上把 README “Old packages” 一节列出的 6 个 1.x 包标记为弃用，并指向替代包：
  - `OpenSlideSharp.Interop`
  - `OpenSlideSharp.Windows`
  - `OpenSlideSharp.runtime.win`
  - `OpenSlideSharp.BitmapExtensions`
  - `OpenSlideSharp.OpenCVExtensions`
  - `OpenSlideSharp.BruTile`
- **如果要重建仓库历史，必须在设置 key 之前完成。**
  - v1.0.1.3、v1.1.0、v1.1.1 所指的提交必须仍是新 `master` 的祖先，或者删除、移动这三个 tag。否则 `Test-ReleaseCommit` 会让每一次发布都失败（“does not contain the latest release”）。
  - 一旦开始发布 4.x，再改写历史会让 4.x 的 tag 也失效。
- **保持仓库设置 “Allow GitHub Actions to create and approve pull requests” 开启。** `master` 在同步期间移动时，同步改为开 PR，这一步需要该设置。

## 合并之后、设置 key 之前

- 合并触发的 push 运行本身就是一次 Release 演练：确认它为绿色，并且没有 tag、Release 或包。
- 在 `master` 上分别 dispatch 一次 Release 和 Upstream sync，都设 `dry_run=true`。sync 的预期结果是 detect 输出 `[]`。
- 检查 Release 运行中的 `viewer check (win-x64)` 与 `viewer check (osx-arm64)`：
  - 两个归档都在本平台上运行 `--check` 成功；
  - Windows 上的 ProductVersion、应用清单和 GUI 子系统都通过检查。
  - win-x64 归档是在 Linux 上交叉发布的。.NET 8 起，SDK 在任何主机上都会把版本资源和清单写进 Windows 可执行文件，不再给出 NETSDK1074。这里第一次在 CI 中实际验证这一点。
  - 再下载 `viewer-win-x64` artifact，在 Windows 桌面上打开一次并查看文件属性；在 Mac 上运行一次 osx-arm64 归档，包括 Gatekeeper 的提示。
- 合并后，第一次遇到上游新构建的每日同步就会以 IOL0ol1 身份向 `master` 提交。sync 的 update → ci → merge → release → report-failure 整条链路，要等上游出现新的 nightly 才会第一次真实运行；没有 key 时，其中的 release 是演练。

## 以后处理

### 发布流程（release.yml 与 eng 脚本）

- **排队中的发布仍可能被同组的后来者取消。**
  - 现在每个 ref 一个并发组（`publish-<ref>`），分支上的演练不再影响 `master`。
  - 但 `master` 上 push 触发的发布和 sync 调用的发布仍在同一组。被取消的 sync 调用以 cancelled 结束，不会开 issue。
  - GitHub 已支持 `concurrency.queue: max`（最多 100 个排队）。actionlint 1.7.12 还不认识这个键，等 actionlint 支持后可以改用。
- **`gh release view … 2> /dev/null` 把所有错误都当成 “Release 不存在”。** 判断附件是否已上传时，只比较文件名，不比较 state 和 size。
- **key 和写权限 token 的暴露面。**
  - key 的判空在 checkout 之后，此前已经运行了仓库里的代码。
  - publish job 的 checkout 把写权限 token 留在 `.git/config`。
  - 可以改为：把判空放到第一步；publish 用 `persist-credentials: false`，只在打 tag 的那一步传入 token。
- **正式版版本号末段为 `.0` 时，NuGet 会规范化文件名**（`X.Y.Z.0` 写成 `X.Y.Z`）。`Get-ReleaseFiles` 和 `Test-Packages` 会在推送之前失败。openslide-bin 4.x 迄今没有这样的版本。
- **部分推送后点错重跑按钮的窄缝。**
  - 较新提交的发布只推了一部分包、tag 尚未推送时，对旧提交的运行点 “Re-run all jobs” 看不到这次部分发布。
  - nuget.org 索引完成之前，flat container 返回 404，而 `--skip-duplicate` 会吞掉 409。
  - 可以在推送前再读取 nuget.org 上最高版本的 nuspec commit，要求它是 `$SHA` 的祖先或 `$SHA` 本身。
- **清单停在已过期的 nightly 时，`Get-ReleaseFiles` 只从上游 URL 下载源码包，会失败。** 可以在失败时回退到本仓库同一 binVersion 的旧 Release 附件（低优先级）。

### 版本号与上游检测脚本

- **`Get-ReleaseVersion` 的输入校验。**
  - 不校验清单的 `date`（`YYYY-MM-DD`）和 `openslideCommit`（至少 8 位十六进制）。
  - `ConvertTo-NuGetVersion` 接受空标识符和带前导 0 的数字标识符。
  - stable 路径也会无条件计算 nightly 基线。
  - 格式错误会在 pack 阶段失败，早于推送。
- **最新 nightly 解析失败时，新的正式版也会被挡住。** 在 `Find-UpstreamUpdate` 中，如果 release 正文缺少 OpenSlide 行，或版本没有 `-g<hash>`，整个脚本失败。同时出现的新正式版，要等 nightly 恢复后才会同步。失败是显式的。

### Upstream sync

- **排队的运行，或 “Re-run all jobs”，用的是触发时的 `github.sha`。** 如果 `master` 已经包含同一构建，会再同步一次，多跑一遍 CI，并开出一个多余的 PR。detect 可以先用 `git ls-remote origin refs/heads/master` 与 `github.sha` 比较。
- **分支和 PR 的清理。**
  - 被取消、或被更新构建取代的运行，会留下 `sync/*` 分支和 PR。
  - report-failure 删除分支前，不检查该分支是否有打开的 PR，会因此关闭同一构建之前开的 PR。

### 打包与许可证

- `Test-Packages` 的 `Assert-DependencyGroups` 忽略不在依赖组里的依赖。目前 SDK 总是写成分组。
- 查看器遇到旧的 `artifacts/native` 布局时，错误信息没有提示运行 `eng/Get-OpenSlide.ps1`。
- 查看器生成 `SOURCE.md` 时，用正则取 `openslide-bin.json` 中第一个 `url`/`sha256`，不看它属于哪个对象。目前只有 `source` 有这两个键，`Publish-Viewer` 和 `Test-Packages` 也会与清单比对。
- 本地或非发布的 publish 生成的 `SOURCE.md` 指向 `releases/tag/v0.0.0-local`，这个 Release 不存在。
- 查看器 `THIRD-PARTY-NOTICES.txt` 中的 `<platform>` 有两种含义：openslide-bin 的平台键，以及 NuGet 包后缀（`Win32`、`Linux`、`macOS`、`win-x64`）。

### 工具

- `TileWriter` 用 `File.WriteAllBytes` 写 `.<name>.tmp`，会跟随这个路径上预先放好的符号链接。只影响 `.dzi`，场景很刻意；计划 5 之前就存在。

## 计划 5 中做出的裁定（摘要）

- **R1**：release.yml 与 upstream-sync.yml 的端到端验证放在合并之后，在 `master` 上用 `workflow_dispatch` 演练。dispatch 只能运行默认分支上的 workflow。
- **R2**：runtime 包和 tool 包用 `PackageLicenseFile`（`LICENSE.txt` 说明随附的各组件，全文在 `licenses/`），不用简化的 SPDX 表达式。随附内容涉及 LGPL-2.1、MIT、BSD、IJG、zlib、MPL、libtiff 等多种许可。
- **R3**：计划 3 遗留的最后一批 API 事项延后：带 `HttpContext` 的异步解析器、命名选项、公开的 `Evict(SlideLease)`。它们都是新增 API，4.x 首次发布之后再加也不破坏兼容。
- **R4**：interop job 的 pip 哈希锁定延后。该 job 只有只读权限，也不接触 secret。
- **R5**：LGPL 源码的长期保存。
  - runtime 包的 `LICENSE.txt` 与 README、tool 包的 `LICENSE.txt`、查看器生成的 `SOURCE.md` 都写明：同版本的 GitHub Release（tag `v<version>`）附带 `openslide-bin-<binVersion>.tar.gz`，可按文件名和 `source.sha256` 校验。
  - 查看器归档只带本 RID 平台的 openslide-bin 许可证。
- **R6**：工具 `deepzoom` 在 `-s`/`-e` 过大时先删除旧输出再失败，属于数据安全回归，合并前必须修复（已修复）。同一任务的其他小问题在收尾时处理，或列入本文件。
- **R7**：正式版的 `InformationalVersion` 也带 openslide 的 commit hash。
  - `Get-ReleaseVersion` 输出 `openslideUpstreamVersion`：`openslideVersion` 已含 `-g<hash>` 时就是它，否则为 `<openslideVersion>.g<openslideCommit>`。
  - `informationalVersion` = `<version>+openslide.<openslideUpstreamVersion>`。
- **R8**：`Find-UpstreamUpdate` 列出 openslide-bin 的全部 release，按 NuGet 顺序取最高的非预发布版本，不用 `releases/latest`。latest 按创建时间排序，旧系列晚发的维护版会把新系列的正式版永远挡住。
- **R9**：nuget.org 上已知存在 `OpenSlideSharp`，所以 flat container 返回 404 或空列表视为错误，而不是“尚未发布”。否则单调检查会悄悄失效。
- **R10**：`.N` 取同一基础版本的已有 tag 与已发布版本中的最大值再加 1。这样包已推送而 tag 没打上时，后续发布不会卡住。
- **R11**：同步调用发布时，传入快进到 `master` 的那个提交的 SHA，而不是 `master`。发布的正是通过同步 CI、并被快进的那个提交。`master` 已经移动时改开 PR，不发布。
- **R12**：同步失败 issue 的去重和标题逻辑放在带 `-SelfTest` 的脚本里（`eng/Report-SyncFailure.ps1`，用模拟的 GitHub API），由 CI 的 scripts job 运行。
- **R13**：merge 退回为 PR 时，保留同步分支（PR 需要它），并跳过发布。`sync/<tag>` 只在快进成功后或失败时删除；之前运行留下的同名分支会被替换。
- **R14**：计划 5 的计划文件中有一句不符合仓库文字规范的措辞，由该计划的首个提交引入。合并前改写功能分支上 df839f7 之后的历史，从所有版本中删去这一句，并强推功能分支；`master` 和已有 tag 不动。
- **R15**：收尾修复轮的范围。
  - 处理合并前必须修复的项、设置 key 之前必须修复的项、整体检查新发现的小问题，以及几处只需改注释或文档的小项；其余可以以后处理的事项记入本文件。
  - 以下几项不再处理：
    - runtime csproj 硬编码四个平台键：`Test-Packages` 会断言许可证目录与清单的平台键一致；
    - `Find-UpstreamUpdate` 接受 3 段 binVersion：openslide-bin 的版本都是 4 段；
    - release.yml 的调用方必须授予 `contents: write`：upstream-sync 已授予，并写了注释；
    - push 触发的路径不含查看器、`eng/` 脚本和 `global.json`：README 已写明这类改动要手动运行 Release。

## 已知问题（维护者决定只写文档）

Windows 上遇到损坏的 JPEG/PNG 会触发 CFG/longjmp 崩溃，整个 .NET 进程被终止，工具、Web 示例和查看器也一样，详见 `docs/known-issues/windows-cfg-longjmp-crash.md`。
