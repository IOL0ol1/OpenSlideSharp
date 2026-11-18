# OpenSlideSharp 4.x 实施路线图

**Spec:** `docs/superpowers/specs/2026-11-17-openslidesharp-v4-refactor-design.md`

规格覆盖五个相互独立、但按顺序依赖的子系统，因此拆成五个计划。每个计划完成后都产出可独立测试的软件；后一个计划在前一个计划执行完毕后再写，以便直接引用已经落地的接口。

| # | 计划 | 范围（规格章节） | 依赖 |
|---|---|---|---|
| 1 | `2026-11-17-plan1-core-binding-runtime.md`（已完成） | 仓库重置、上游清单与下载脚本、ClangSharp 生成、核心 `OpenSlide` API、原生库加载、`OpenSlideSharp.runtime`（.NET + VS C++）、基础 CI（§5、§6.1 绑定部分、§6.2、§7.1、§8 前半） | — |
| 2 | `2026-11-17-plan2-rendering.md`（已完成） | `PixelImage` 合成与缩放、共享渲染流程、`OpenSlide.GetThumbnail`、`DeepZoomGenerator`、`IColorTransform`、`OmeZarrGenerator`、`OpenSlideSharp.SkiaSharp`（§6.1 其余、§6.3、§8 中 DeepZoom / Skia / OME-Zarr） | 1 |
| 3 | `2026-11-18-plan3-web-tool.md`（已完成） | `OpenSlideSharp.AspNetCore`、`samples/OpenSlideSharp.Web`、`OpenSlideSharp.Tool`（§6.4–§6.6） | 2 |
| 4 | plan4-viewer（待写） | `samples/OpenSlideSharp.Viewer`（Avalonia）（§6.7） | 2 |
| 5 | plan5-release（待写） | 上游同步、版本号计算、发布、GitHub Release、README 重写、旧包弃用清单（§7.2、§7.3、§9） | 1–4 |

计划 3 与计划 4 互不依赖，可以并行。
