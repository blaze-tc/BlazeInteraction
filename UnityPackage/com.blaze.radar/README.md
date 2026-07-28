# Blaze Radar SDK 1.2.0

Unity 2021.3+ 的 FaseLase F10/F20 多屏、多雷达、多指针输入包。包内 `Bridge~/win-x64` 是完整 self-contained RadarBridge 发布目录；Editor 可自动启动，Windows Player 构建时会复制整个目录并校验版本与 SHA-256。

固定版本安装 URL：

```text
https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.0
```

从 1.1.x 升级必须先移除旧 Git URL，重新解析后在 Package Manager 确认 Blaze Radar SDK `1.2.0` 的 Resolved Path 指向当前 `Library/PackageCache/com.blaze.radar@...`。

在 **Project Settings > Blaze Radar** 定义稳定 Screen ID、逻辑分辨率、顺序和唯一 Primary；导入 **Basic Interaction** 做标准 EventSystem 检查，导入 **Multi-Screen Camera Routing** 做 LOCAL/BRIDGE IPC 及 Display、Camera `pixelRect`、RenderTexture 路由检查。详见 `Documentation~/index.md`。
