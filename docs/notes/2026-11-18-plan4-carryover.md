# 计划 4 遗留事项（供计划 5 及以后使用）

来源：计划 4（Avalonia 桌面查看器）各任务审阅、整体终审及其修复轮。下列事项都已评估为**不阻塞计划 4 合并**。

## 计划 5（发布）之前必须处理

- **发布物形态。**
  - 查看器的发布目录包含单文件可执行程序和 `licenses/`。`licenses/` 下有三部分：
    - openslide-bin 的许可证与 `VERSIONS.md`；
    - `THIRD-PARTY-NOTICES.txt`；
    - `SOURCE.md`。
  - 每个 RID（win-x64、linux-x64、osx-arm64）把整个目录打成一个 zip 或 tar.gz 附到 GitHub Release，不能只附可执行文件。
- **`THIRD-PARTY-NOTICES.txt` 中 MicroCom.Runtime 与 Tmds.DBus.Protocol 只写了 “MIT. Copyright …”。** 发布前补上 MIT 许可全文，或注明下方的 MIT 文本同样适用于这两项。
- **`SOURCE.md` 只给出了 openslide-bin 源码包的 URL 模式。** runtime 包没有记录确切的 openslide-bin 版本。计划 5 的发布流程要把版本写进 runtime 包（或发布目录），并在 `SOURCE.md` 中给出确切链接。
- **macOS：** 二进制没有签名和公证，Gatekeeper 会拦截。需要选择一种处理方式：
  - 在 Release 说明里写明 `xattr -d com.apple.quarantine <文件>` 的绕过方法；
  - 或者在 CI 中签名并公证，这需要维护者提供 Apple 开发者证书。
- **查看器 README 里的发布构建写的是 “planned”。** 发布流程上线后要更新这一段。

## 查看器（低优先级，可继续延后）

- **渲染与加载：**
  - 层级选择没有滞回。可见瓦片数随平移对齐变化（c×r 与 (c+1)×(r+1)），在临界缩放区间平移时，层级会在 L 与 L+1 之间来回切换。可以改用与对齐无关的上界，或加入滞回。
  - Bitmap 构造持续失败（例如持续 OOM）时，会无限地“读取 → 失败 → 移除 → 再读取”，没有退避。
  - 单层、超过 16 Mi 像素、又没有 `thumbnail` 关联图像的切片没有底图，适配窗口时只显示中心附近的瓦片。
  - 每次平移移动会触发两次 `ViewChanged`。
- **窗口：**
  - 如果 `Show()` 在视图已经接受新切片之后才失败（极少见，例如 OOM），状态栏会报错，但视图已在显示新切片，标题和属性却还是旧的。
  - 导航器拖动没有区分指针身份。
  - 很长的错误文本会挤占状态栏其他段。
  - 文件选择器只用了 `Patterns`，macOS 上没有 UTI 过滤。
  - 选区列表每次变化都整体重建，选中状态会丢失。
  - `--check` 在 Windows 上附加父控制台后，没有重新绑定 `Console.Error`。
- **视口：**
  - `Resize(0,0)` 会重新钳制缩放，导致缩放状态丢失。
  - 文档中 “永不为 NaN” 的说法过强：有限但极大的输入会让中心变成 ∞。
- **测试：**
  - 没有测试锁定 “Bitmap 构造时会复制像素” 这一点（应在复制后改写缓冲区再断言）。
  - `ConsumePixels` 的文档措辞不准确。
  - RapidZoom 测试只比较数量。
  - 打开失败的测试没有断言关联图像、导航器和选区保持不变。
  - 窗口级的 “无 mpp” 路径只在 `StatusFormatter` 中有单元测试。

## 计划 4 中做出的裁定（摘要）

- **R1**：范围按规格 §6.7，只做矩形选区和单切片；不做自由手绘、椭圆选区和双切片联动。
- **R2**：Task 1 的三个后续问题（偶发失败的测试、回调异常保护、位图释放与缓冲池）并入 Task 2 处理。
- **R3**：按设备像素的 downsample 选择层级，以保证高 DPI 下清晰。
- **R4**：先请求粗层瓦片，实现“先显示低分辨率”；加载失败的标记按切片保存；拖动只跟随按下它的那个指针。
- **R5**：Windows 下 `--check` 附加父控制台输出。
- **R6**：目标层可见瓦片的字节数控制在缓存预算的一半以内，按离中心距离截断请求，并用导航缩略图作底图，保证加载一定能结束。
- **R7**：发布目录附带 `licenses/`。替换原生库的方式是设置 `OPENSLIDE_LIBRARY_PATH`。
- **R8**：切换或关闭切片时不阻塞 UI 线程，依赖核心库的 SafeHandle 租约保证安全。

## 已知问题（维护者决定只写文档）

Windows 上遇到损坏的 JPEG/PNG 会触发 CFG/longjmp 崩溃，查看器进程同样会被终止，详见 `docs/known-issues/windows-cfg-longjmp-crash.md`。
