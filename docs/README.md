# BlazeInteraction 文档总览

以下文档描述 `v1.1.1` 的当前行为。首次使用建议按“安装 → 图文功能 → Unity 集成”的顺序阅读；修改代码前阅读“架构 → 开发指南 → 对应模块文档”。

## 使用与部署

| 文档 | 用途 |
| --- | --- |
| [安装与升级](../INSTALL.md) | `.tgz`、Git、本地源码安装；Player 发布与换机验收 |
| [图文功能说明](user-guide.md) | 设备选择、Radar、CameraVision、Unity 示例界面与操作 |
| [Unity 集成](unity-integration.md) | Surface、Runtime、API、Camera 路由、Samples |
| [故障排查](troubleshooting.md) | 连接、配置、相机、雷达、版本和性能问题 |
| [版本与限制](version-and-limitations.md) | 1.1.1 身份、能力边界和现场限制 |

## 维护与扩展

| 文档 | 用途 |
| --- | --- |
| [架构与数据流](architecture.md) | 模块所有权、Provider 生命周期、IPC/Unity 边界 |
| [仓库结构](repository-structure.md) | 每个目录的源代码、生成物和责任人入口 |
| [代码维护与扩展](development-guide.md) | 常见修改路径、测试矩阵、版本和发布流程 |
| [Provider API 1](provider-development.md) | 新感应设备插件的目录、接口、生命周期和测试 |
| [Interaction IPC 1](protocol.md) | framing、握手、InteractionPoint、背压和身份校验 |
| [Radar 标定](calibration.md) | Radar 过滤、四点标定与输出空间 |
| [Radar 迁移](radar-migration.md) | 从旧 `com.blaze.radar` 迁移 |

## 验证与历史记录

- [v1.1.1 发布说明](release-notes/v1.1.1.md)：CameraVision 标定框预览坐标修复与安装入口。
- `docs/verification/`：性能和稳定性验证证据。
- `docs/camera-vision/`：CameraVision 早期 Gate 分析/报告。
- `docs/*-test-report.md`：对应阶段的测试快照。
- `docs/superpowers/specs/`：已批准的设计规格。
- `docs/superpowers/plans/`：实施计划和检查点。

这些文件用于追溯“为什么这样实现”，可能保留当时的版本号、Gate 名称或未实现描述。发生冲突时，以当前源码、根目录 README、INSTALL 和本页“使用与部署/维护与扩展”中的当前文档为准。

## 文档维护规则

公开 API、配置 Schema、IPC、菜单、目录布局或版本发生变化时，同一提交必须更新相关当前文档。截图应保存在 `docs/images/`，使用相对路径引用；不要把包含个人桌面、密钥、客户画面或绝对路径的截图提交到仓库。
