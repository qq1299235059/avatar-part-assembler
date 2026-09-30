# Avatar Part Assembler 中文入口

Avatar Part Assembler（APA）的主要项目说明现在统一维护在 [`README.md`](README.md)。

当前版本：`0.5.4` · Unity `2022.3`。

## 文档导航

- [项目介绍、安装、快速使用与功能边界](README.md)
- [部件制作指南](Documentation~/PART_AUTHORING_GUIDE.zh-CN.md)
- [技术总览 / 架构与诊断注册表](Documentation~/OVERVIEW.md)
- [变更历史](CHANGELOG.md)
- [人工验收清单](USER_ACCEPTANCE_CHECKLIST.md)
- [安全与隐私说明](SECURITY.md)
- [许可证](LICENSE.md)

如果你只是安装别人制作好的 APA 部件，从 [`README.md`](README.md) 的“Avatar 使用者：安装一个部件”开始即可。

如果你要制作并发布部件，请直接阅读[部件制作指南](Documentation~/PART_AUTHORING_GUIDE.zh-CN.md)。

受保护网格模式使用混淆和完整性校验来减少源网格的直接分发，不是不可提取的 DRM。编解码逻辑随公开包分发，
构建时会在内存中还原网格；预制体与旁边的 `_ProtectedMesh.asset` 必须一起交付。
