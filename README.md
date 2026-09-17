# Avatar Part Assembler

面向 VRChat 模块化角色身体部件的**非破坏性、确定性**装配工具。

> **Builder 不猜，Validator 负责阻止错误资产进入构建。**
> The builder does not guess; the validator keeps bad assets out of the build.

- 版本：`0.3.0-rc.3`（发布候选，不是 1.0）
- Unity：2022.3
- 界面语言：English / 简体中文

部件作者发布一个预制体，用户把它拖到自己的角色下即可。插件移除原身体上被声明的区域，在**顶点级别**把部件的接缝焊接到身体的接缝上，并把几何体、蒙皮、形态键、UV 语义、材质语义和骨骼合并成**每个目标渲染器一张生成网格**。把预制体删掉，角色与之前完全一致——因为工具从未写入任何制作资产。

---

## 安装

先准备好这些依赖：

| 依赖 | 版本 |
| --- | --- |
| Unity | 2022.3 |
| VRChat SDK – Avatars | `>=3.10.4 <3.11.0` |
| NDMF | `>=1.14.0 <2.0.0-a` |
| Modular Avatar | `>=1.18.0-beta.0 <2.0.0-a` |

然后 Unity Package Manager → `+` → **Add package from git URL**：

```
https://github.com/qq1299235059/avatar-part-assembler.git
```

`package.json` 就在仓库根目录，所以不需要 `?path=` 后缀。想锁版本可以写成 `...git#v0.3.0-rc.3`。

> 用 VCC / VPM 的话：本仓库没有提供 listing，请走上面的 git URL 通过 UPM 添加。

## 三分钟上手

**部件作者**

1. 把部件摆到与身体一致的姿态，选中部件根节点。
2. 打开 `Tools / Avatar Part Assembler / Part Authoring`（中文菜单 `Tools / 部件装配器 / 部件编辑`）。
3. 按窗口的 12 个 section 依次填：身份与部件槽 → 目标渲染器 → 移除区域 → 接缝配对 → UV / 材质语义 → 骨骼与形态键策略。
   - 移除区域有三种录入方式，任选其一：Scene View 拾取、数值地址、黑白遮罩纹理（固定 7 点采样规则）。
   - 接缝由 `ApaSeamWorldMatcher` 在**世界空间**生成一对一配对并写进 Profile。
4. 保存。所有写入都经过唯一入口 `ApaProfileWriter`，产出 `ApaPartProfile` 资产和带 `AvatarPartInstaller` 的预制体。

**使用者**

把部件预制体拖到角色下就可以。编写期预览、Play Mode / Gesture Manager 测试、真实上传构建走的是同一条流水线。

## 它真正在解决什么

| 问题 | 做法 |
| --- | --- |
| 结果不可复现 | 禁用进程随机化哈希（`System.HashCode` / `GetHashCode`），统一 FNV-1a 64；所有排序都是显式 ordinal 键；字典遍历前必须先排序 |
| 弄脏作者资产 | `MeshSnapshot` 是带所有权语义的不可变托管副本，核心流水线只读、绝不写回 |
| 接缝靠猜 | 接缝必须是 Profile 里存下来的**一对一显式配对**（`PairingVersion = ExplicitPairing`），构建期不做任何位置搜索，legacy 未配对直接报 `APA042` |
| 构建半途炸掉 | `ApaBuildProcessor.Process` 是六步事务：前五步不改动场景，任一装配组失败则整体逆序回滚，不留半成品 |
| 预览和上传不一致 | 预览与构建共用同一门面 `ApaCore`、同一事务、同一诊断类型，唯一差异是 `allowPostMergePartArmatureScope` |
| 报错说不清 | APA001–APA046 / APA050 / APA999，一码一义、退役码不复用；无法恢复的字段一律硬拒绝并给出稳定 `reason=` token |

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| `Runtime/` | 契约层：`ApaPartProfile`、`AvatarPartInstaller`、诊断类型、数值策略、骨骼签名（随包发布，让 prefab 自描述） |
| `Editor/` | 核心流水线：`ApaCore` 门面、网格快照、11 条校验规则、解析、装配规划、`MeshAssembler` |
| `Editor/NDMF/` | NDMF 插件、两个 pass、`ApaBuildProcessor` 事务处理器 |
| `Editor/Preview/` | NDMF 实时预览、指纹缓存与 LRU 租约 |
| `Editor/Authoring/` | 部件编辑窗口、移除 / 接缝拾取、Profile 写入器 |
| `Tests/Editor/` | 410 个测试，含基于源码文本扫描的静态契约测试 |
| `Documentation~/` | 完整英文产品文档（Unity 不会导入该目录） |
| `README.zh-CN.md` | 完整简体中文使用文档 |
| `CHANGELOG.md` | 变更历史 |
| `USER_ACCEPTANCE_CHECKLIST.md` | 验收清单 |

## 文档

- 完整英文产品文档（策略表 + 诊断注册表）：[`Documentation~/OVERVIEW.md`](Documentation~/OVERVIEW.md)
- 完整简体中文使用文档：[`README.zh-CN.md`](README.zh-CN.md)
- 变更历史：[`CHANGELOG.md`](CHANGELOG.md)
- 验收清单：[`USER_ACCEPTANCE_CHECKLIST.md`](USER_ACCEPTANCE_CHECKLIST.md)
- 第三方依赖：[`Third Party Notices.md`](Third%20Party%20Notices.md)

## 当前状态

发布候选。真实 NDMF 运行时构建路径已经在 Unity 2022.3.22f1 上跑通，但**完整验收仍未完成**：Scene View 预览的完整视觉验收、更多生态组合（AAO、lilToon、PhysBone、Contacts、prefab variant 等）、以及端到端 VRChat 上传都还在清单里。

也就是说：核心路径可用，但请把它当作 RC 而不是稳定版，上传前自己过一遍 `USER_ACCEPTANCE_CHECKLIST.md`。

## 许可证

MIT，见 [`LICENSE.md`](LICENSE.md)。
