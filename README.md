# TianNanGe · 天南阁

使用 Unity HDRP 复刻参考插画中的雪桥、巨龙、人物与东方楼阁，先搭简单大形，再逐步细化。

- [项目文档](llmdoc/index.md)
- Unity 项目：`Project_TianNanGe/`
- 参考图：`tiannan_by_wlop_dfuoumd-fullview.jpg`

当前已接入 Kloofendal HDRI、MAXDESIGN-3D 临时动画龙及 Little Green，人物已配置 WASD 行走与碰撞，主要场景对象已整理为 13 个 Prefab。桥已延长至 38 米并增加两端连接台，四座楼阁由雪山岩体承托；人物右手已挂接独立佩剑预制体。场景与资源检查 38/38、人物移动/动画/碰撞运行验证 16/16 全部通过。

[主场景](Project_TianNanGe/Assets/TianNanGe/Scenes/TianNanGe_Main.unity) · [当前人物与佩剑预览](captures/tiannange-improvements-play.png) · [桥与山体全景](captures/tiannange-mountain-overview.png) · [此前人物预览](captures/little-green-play.png) · [临时龙阶段预览](captures/tiannange-temporary-dragon.png) · [HDRI 白盒基线](captures/tiannange-hdri.png) · [首版白盒](captures/tiannange-blockout.png)。后续精模由 Tripo 制作，新骨架与动画需重新适配；来源、许可与 Prefab 路径见项目文档。

打开工程前，在仓库根目录运行 `git lfs install` 和 `git lfs pull`。使用 Unity `6000.3.10f1` 打开 `Project_TianNanGe/`，加载主场景 `Assets/TianNanGe/Scenes/TianNanGe_Main.unity`；点击 Play 后点击 Game 视图，用 WASD 或方向键移动，松键返回冻结的暂用站姿。
