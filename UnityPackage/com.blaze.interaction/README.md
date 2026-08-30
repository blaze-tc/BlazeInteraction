# Blaze Interaction SDK

Unity 2021.3+ 的统一 Radar / CameraVision 输入包。

安装后在 **Project Settings > Blaze Interaction** 配置 Surface，执行 **GameObject > Blaze Interaction > Create Runtime**，再从 Package Manager 导入 **Basic Interaction** Sample。

运行时：

- `InteractionPoint.PixelPosition` 是通用中心点；
- `InteractionPoint.Fp` 在 Radar 下为实际扫描点，在 CameraVision 下为 21 个手部骨骼点；
- 配置按当前 Unity 项目或 Player 的 persistent data 隔离保存。

完整文档：https://github.com/blaze-tc/BlazeInteraction
