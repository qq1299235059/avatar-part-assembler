# Avatar Part Assembler

Avatar Part Assembler（APA）是一个面向 VRChat 模块化 Avatar 部件的 Unity/NDMF 插件。
它把部件安装声明、目标身体三角形移除、接缝焊接、蒙皮、形态键、UV、材质与骨骼处理放进同一条
可预览、可验证、可回滚的构建流水线。

> APA 不修改作者的原始网格，也不把部件永久写进 Avatar。预览和上传构建都在 NDMF 的临时对象上完成。

当前版本：`0.4.0` · Unity `2022.3` · 界面支持 English / 简体中文

## 先看哪一份文档

- 部件作者：[`Documentation~/PART_AUTHORING_GUIDE.zh-CN.md`](Documentation~/PART_AUTHORING_GUIDE.zh-CN.md)
  ——从建模准备到发布部件的完整步骤、资产清单和强制约束。
- Avatar 使用者：直接阅读下面的“安装部件”部分。
- 技术实现、装配策略和诊断注册表：[`Documentation~/OVERVIEW.md`](Documentation~/OVERVIEW.md)。
- 变更历史：[`CHANGELOG.md`](CHANGELOG.md)。
- 人工验收清单：[`USER_ACCEPTANCE_CHECKLIST.md`](USER_ACCEPTANCE_CHECKLIST.md)。

## 安装

依赖环境：

| 依赖 | 版本 |
| --- | --- |
| Unity | `2022.3` |
| VRChat SDK – Avatars | `>=3.10.4 <3.11.0` |
| NDMF | `>=1.14.0 <2.0.0-a` |
| Modular Avatar | `>=1.18.0-beta.0 <2.0.0-a` |

在 Unity 的 Package Manager 中选择 **+ → Add package from git URL**，填入：

```text
https://github.com/qq1299235059/avatar-part-assembler.git
```

也可以在 VCC/VPM 中添加发布清单后安装。锁定版本时使用仓库已有的 tag，例如：

```text
https://github.com/qq1299235059/avatar-part-assembler.git#v0.4.0
```

## 安装一个已有部件

1. 将部件作者提供的 `.unitypackage`、VPM 包或部件目录导入工程。
2. 把部件预制体拖到 Avatar 根对象下。预制体根节点应带有 `AvatarPartInstaller`。
3. 确认安装器上的 Profile 已存在，且组件处于启用状态。
4. 在 NDMF 预览中检查结果，再进入 Play Mode/Gesture Manager 测试；最终上传时仍由同一条 NDMF 流程处理。
5. 卸载时删除部件预制体。APA 不会把修改写回原 Avatar 网格。

安装器默认启用“跟随 Avatar 骨骼”和“包含缩放”，它们是编辑器中的摆放辅助，不会改变 Profile 的构建数据。
部件制作、导出和兼容性约束请按[部件制作指南](Documentation~/PART_AUTHORING_GUIDE.zh-CN.md)执行。

## APA 的工作方式

- Profile 保存稳定部件 ID、目标网格签名、移除三角形集合、显式接缝配对、UV/材质语义、骨架路径和形态键策略。
- 当前新建 Profile 使用 Schema 5；旧 Profile 不会被静默改写，缺少必要的骨架、配对或指纹信息时会要求重新制作。
- 接缝在制作阶段按世界坐标生成一对一配对；构建阶段只消费已保存的配对，不会重新猜测。
- 部件接缝候选由部件网格的 `Mesh.colors32` 精确颜色筛选；目标身体仍按空间位置匹配，不要求目标网格有顶点色。
- 目标身体和部件网格会被读取为不可变快照，输出是每个目标渲染器对应的一张生成网格。
- 预览、Play Mode/Gesture Manager 和上传构建共享同一个 NDMF 处理器；非法输入会在生成任何网格前阻断。
- Profile 与网格会使用内容指纹校验。网格重新导入后，即使顶点数量没有变化，只要属性改变，也会要求重新捕获 Profile。
- 同名的 Modular Avatar Merge Animator 动画会在装配后重定向到合并目标渲染器；空的部件源对象会在构建结果中清理。

## 最重要的作者约束

以下不是“建议”，而是部件能否可靠发布的边界：

1. 目标身体必须是 `SkinnedMeshRenderer`，目标渲染器在选定 Avatar 根对象之下；部件渲染器必须在部件根对象之下。
2. 所有需要保存进 Profile 或预制体的引用都必须是项目资产或预制体内部引用，不能引用制作场景里的临时对象。
3. 部件 ID 必须稳定且唯一。不要用部件显示名称、父节点名称或当前层级路径代替 ID。
4. 只对部件网格绘制接缝候选颜色。颜色按 `Color32` 四通道精确比较，默认色号为 `#000000`；不要依赖颜色渐变、抗锯齿或只改变 RGB 而忽略 alpha。
5. 接缝顶点不得携带 Avatar 没有的有效骨骼权重。所有有效权重必须指向部件骨架内真实骨骼；有效权重之和必须大于 `1e-5`，且不能为负数、NaN 或 Infinity。
6. 目标骨架和部件骨架必须显式选择，并且层级相互对应。相同的相对骨骼路径表示同一个关节，目标身体的骨骼拥有最终权威；不要在部件中为同一关节制作另一套不对应的权重骨骼。
7. 需要真正焊接的接缝顶点必须处于同一静置姿势，并在默认 `1e-4` 世界单位容差内重合。不要通过放宽容差来掩盖建模错位。
8. 如果接缝两侧同名 UV 语义不同，APA 会保留部件侧的分裂顶点而不是悄悄丢 UV。需要焊接的 UV 必须在对应语义下相同。
9. 同名形态键必须拥有相同的帧数量和帧权重；部件独有形态键在焊接接缝上的位置、法线和切线增量必须为零。
10. 所有材质必须是项目中的 Material 资产。不要把只存在于场景中的材质拖进 Profile。

违反这些约束时，优先修复源资产，不要把验证错误当成可以忽略的警告。

## 构建结果与安全性

APA 采用事务式装配：规划、验证、网格生成和清理任何一步失败，都会阻止该组输出；不会留下半生成网格。
原始场景网格、Avatar 预制体和部件作者的源资产不会被写回。生成的配置文件和部件预制体是可移植发布物，
其中不应包含制作场景对象的引用。

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| `Runtime/` | Profile、Installer、诊断和运行时数据契约 |
| `Editor/Authoring/` | 部件编辑窗口、Profile/Prefab 写入、候选与预览叠加层 |
| `Editor/Assembly/` | 网格、蒙皮、UV、材质、形态键和骨骼装配核心 |
| `Editor/NDMF/` | 预览、Play Mode 和上传构建入口 |
| `Editor/Validation/` | 兼容性、接缝、权重和属性验证规则 |
| `Tests/Editor/` | EditMode 与源码契约测试 |
| `Documentation~/` | 面向作者和维护者的文档 |

## 许可证

MIT，见 [`LICENSE.md`](LICENSE.md)。
