# 天南阁 Volume

预制体：`Assets/TianNanGe/Prefabs/Lighting/TianNanGeVolume.prefab`。
已嵌入 `LightingRig.prefab`，主场景加载 LightingRig 时自动生效。引用同一 LightingRig 的其他场景也会继承这套配置。

参考 Snow 的环境、阴影、后期分组，为天南阁重建原生 HDRP 17 配置。保持楼阁、人物和雪桥可读，不依赖 Kino 或硬件光追。

| 子对象 / Profile | Priority | 参数与用途 |
| --- | --- | --- |
| 00 Sky / TianNanGe_Sky | 110 | Kloofendal HDRI；18000 lux；旋转 163.72673°；动态天空环境光 |
| 10 Fog / TianNanGe_Fog | 120 | 体积雾；衰减距离 180 m；基础高度 −5 m；最大高度 35 m；体积计算距离 128 m；Custom / Manual，25% 屏幕分辨率、64 层切片，Gaussian 去噪 |
| 20 Shadow / TianNanGe_Shadow | 130 | AO 强度 0.85、半径 1.15、直接光影响 0.25；微阴影 0.7；接触阴影长度 0.2、距离 60 m；主阴影距离 180 m、4 级联 |
| 30 Post / TianNanGe_Post | 140 | 固定 EV100 12、ACES；对比度 −8、饱和度 −7；轻微冷阴影 / 暖高光；Bloom 0.18、阈值 1、扩散 0.65 |
| 40 Lens / TianNanGe_Lens | 150 | 暗角 0.16；Thin1 颗粒 0.035；显式关闭动态模糊、景深、色差和 Panini |

所有 Volume 为 Global，Default Layer，Weight=1。不同层按功能分开，避免 Snow 中两层 Fog 重复覆盖接触阴影的问题。Profile 资产由所有预制体实例共享，直接编辑 Profile 即可调节效果。

## 场景配套

- LightingRig 原 `Global Look` 保留但禁用。其优先级为 100；新五层为 110～150。
- LightingRig 的 Sun 开启接触阴影，阴影分辨率由 512 提升到 2048；Cold Fill 保持原设置。
- 主场景 HDRP 相机使用 SMAA High 与 Dithering，Volume Layer Mask 包含 Default。相机变更保存为主场景覆盖，没有修改 Starter Assets 原始相机预制体。
- 当前 HDRP High Fidelity 支持体积雾和 AO。SSR 在管线资产中关闭，因此此预制体没有添加无效 SSR Override。
- `StaticLightingSky` 仍引用原 Kloofendal Profile；新 Sky 的 HDRI、旋转、强度与其一致。以后修改 Sky 这些参数时，应同步静态天空设置。

## 调整与比较

先调 `Fog.meanFreePath`（增大更通透），再调 `Post` 曝光 / 对比度；阴影过重时降低 `Shadow` AO，四角过暗时降低 `Lens` 暗角。

要比较旧的 Volume，在场景中禁用 `TianNanGeVolume`、启用同级 `Global Look`；比较后恢复。这样只切换 Volume，Sun 和相机改进仍然保留。

生成脚本：`AgentScripts/BuildTianNanGeVolume.cs`，通过 Pipeline `run_script` 运行 `BuildTianNanGeVolume.Build`。它仅用于首次安装，检测到已有预制体或 Profile 目录会拒绝覆盖，以保留后续手工调整。

## 验证（2026-10-19，北京时间）

- Unity 6000.3.10f1 / HDRP 17.3.0；构建脚本编译成功，资产 GUID / 子资源引用有效。
- Play Mode 的帧计数 762 → 770，确认游戏循环推进。
- 从主相机 HDCamera 的最终 Volume Stack 读取：五层各 Weight=1；体积雾 180 / Custom / Manual / 25% / 64，AO 0.85，EV12，对比度 −8，Bloom 0.18，暗角 0.16，MotionBlur 0，DOF Off。
- 主相机 Frame Settings 的 Volumetrics、SSAO、ContactShadows、Postprocess、Bloom、ColorGrading 均为 true；Console 当前实际计数 0 error / 0 warning。
- 已退出 Play Mode。未做独立构建或不同硬件帧率测试。
- 仓库 `captures/tiannange-volume-before.png` 与 `tiannange-volume-after.png` 为同镜头编辑态 1280×720 对比；`tiannange-volume-play-camera.png` 为运行时相机预览。
