# Avatar Part Assembler 中文文档

这是 Avatar Part Assembler 的简体中文入口文档。

## 使用者

安装插件后，把部件作者提供的 `.prefab` 拖到 Avatar 根对象下即可。预制体根节点上的
`AvatarPartInstaller` 会读取部件 Profile，并在 NDMF 预览、Play Mode/Gesture Manager 和上传构建中使用同一套装配流程。
删除预制体即可卸载；APA 不会把网格永久写回 Avatar。

## 部件作者

请按完整的[部件制作指南](Documentation~/PART_AUTHORING_GUIDE.zh-CN.md)操作。指南包含：

- 需要创作和发布的网格、材质、顶点色、Profile、预制体、遮罩和动画资产；
- 从参考 Avatar、模型导入、部件编辑器到保存 Profile/生成普通预制体的逐步流程；
- 接缝顶点色、世界坐标配对、UV 语义、骨骼权重、目标骨架权威和形态键的强制约束；
- Modular Avatar Merge Animator 动画重定向与空源对象清理的验证方法；
- 发布前清单和常见 `APAxxx` 诊断的处理方向。

## 技术文档

- [英文技术总览](Documentation~/OVERVIEW.md)
- [变更历史](CHANGELOG.md)
- [人工验收清单](USER_ACCEPTANCE_CHECKLIST.md)
- [许可证](LICENSE.md)

当前版本：`0.4.0`，Unity `2022.3`。
