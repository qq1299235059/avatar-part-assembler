# 部件装配器（Avatar Part Assembler）

面向 VRChat 模块化角色身体部件的非破坏性、确定性装配工具。

部件作者发布一个预制体（prefab）。用户把这个预制体拖到自己的角色下即可。插件会移除原身体上被声明的
区域，在顶点级别把部件的**接缝**焊接到身体的接缝上，并把几何体、蒙皮、形态键、UV 语义、材质语义和骨骼
合并为每个**目标渲染器**一个生成的网格。删除该预制体后，角色与之前完全一致，因为工具从未写入任何制作
资产。

> **Builder 不猜，Validator 负责阻止错误资产进入构建。**
> The builder does not guess; the validator keeps bad assets out of the build.

英文文档见 [`Documentation~/OVERVIEW.md`](Documentation~/OVERVIEW.md)。本文是简体中文使用文档；两份文档描述同一个产品，如有冲突，以实际
运行的 Unity 编辑器为准，并请把文档当作缺陷报告。

---

## 发布候选状态

**当前版本是发布候选 `0.3.0-rc.3`（见 `package.json`）。它不是 1.0，完整验收清单仍未全部完成。**

核心构建链已经不再只是源码评审：我们已经在 Unity 2022.3.22f1 中实际执行过 NDMF
`AvatarProcessor`。真实构建成功替换了目标身体网格、消费了部件 Renderer 与 Installer，并把同路径的部件骨骼
重定向到身体骨骼；本次构建只产生接缝、UV 保留和骨骼重定向的 Information 级诊断。Editor、NDMF、Preview
和 Editor Test 程序集也已经使用 Unity 实际生成的 Bee/Roslyn 引用集完成零错误编译。

完整用户验收计划仍见 [`USER_ACCEPTANCE_CHECKLIST.md`](USER_ACCEPTANCE_CHECKLIST.md)。Scene View 预览的完整
视觉验收、更多第三方插件组合以及最终 VRChat 上传仍属于后续验收项。若本文与实际 Unity 编辑器行为冲突，
以编辑器实际行为为准，并把文档差异视为需要修复的问题。

当前运行结果也**不**等于“所有生态组合均已验收”。尤其是完整 Scene View 预览验收、每种可选第三方插件
组合和端到端 VRChat 上传仍需继续检查。
## 环境要求

| 依赖 | 版本 | 声明位置 |
| --- | --- | --- |
| Unity | **2022.3.22f1**（`2022.3` 系列） | `package.json` → `unity` |
| VRChat SDK — Avatars | **3.10.4** | `package.json` → `vpmDependencies` |
| NDMF（*Non-Destructive Modular Framework*） | **1.14.0** | `package.json` → `vpmDependencies` |
| Modular Avatar | **1.18.0-beta.0** | `package.json` → `vpmDependencies` |
| `com.unity.modules.animation` | 1.0.0 | `package.json` → `dependencies` |
| `com.unity.test-framework` | 2022.3 自带的 1.1.x | **不是**本包声明的依赖，而是项目依赖，仅在运行 EditMode 测试时需要 |

VPM 版本范围是 `>=3.10.4 <3.11.0`、`>=1.14.0 <2.0.0-a`、`>=1.18.0-beta.0 <2.0.0-a`。

---

## 安装

### 作为内嵌包（本项目）

包已位于 `Packages/dev.avatar-part-assembler/`，Unity 会在下一次域重载时自动识别，无需修改 manifest。

### 作为 VPM 包（对外分发）

把仓库地址加入 VPM 客户端（VCC 或 ALCOM），或安装 `.zip` 发行包。包在 `vpmDependencies` 中声明了依赖，
NDMF、Modular Avatar 与 VRChat SDK 会被自动解析。

### 让测试程序集可见

测试程序集由 `UNITY_INCLUDE_TESTS` 保护，且**不会**被自动引用。若要让它出现在 Test Runner 中，请把本包
加入项目 manifest 的 `testables`：

```json
{
  "testables": [
    "dev.avatar-part-assembler"
  ]
}
```

这是项目级改动，本包刻意**没有**替你做，因为包的改动范围限定在 `Packages/dev.avatar-part-assembler/**`。
未加入 `testables` 时，Unity 通常不会把包内测试程序集纳入测试编译，Test Runner 也不会显示它；因此不能用
普通 Console 没有测试源码报错来代替运行测试。加入后再确认测试程序集编译，并在 Test Runner 中执行它。

---

## 语言切换
## Play Mode 与 Gesture Manager

`0.3.0-rc.3` 默认启用 Play Mode 兼容层。只要已加载场景中存在 `AvatarPartInstaller`，APA 会在进入
Play Mode 前临时打开 NDMF 官方的 **Apply On Play**。

APA 的预览构筑现在发生在 Unity 生成 **Play Mode 临时场景副本**时，通过 `IProcessSceneWithReport` 直接运行
NDMF。这个阶段早于场景组件的 `Awake` / `Start`，因此 Gesture Manager 或其他 Avatar 模拟器还没有机会把骨骼
切换到站立 Pose，APA 就已经完成 NDMF Generating → Modular Avatar Transforming → APA Transforming，并把
合并后的 Mesh 交给后续模拟器。这个早期预构筑不会再调用整套 VRChat preprocess 回调，因此不会消耗或撞上
VRCFury 对 Play Mode preprocess 的“一次性执行”保护。

这里不会再使用 `EnteredPlayMode` 之后的二次构筑，也不会在模型已经被模拟器摆 Pose 后调用
`Animator.Rebind()` 试图补救。这样可以避免“顶点仍按 T-Pose 构筑、骨骼却已经处于站立 Pose”导致的 bind pose
错位和手臂严重扭曲。

整个过程只修改 Unity 的 Play Mode 临时场景副本，不会写回制作场景、Prefab 或模型资产。APA 会通过
`SessionState` 记住进入 Play Mode 前原本的 NDMF Apply On Play 值，并在回到 Edit Mode 后恢复。若你确实想
观察未经处理的制作态角色，可以在
`Tools > Avatar Part Assembler > Play Mode + Gesture Manager Compatibility` 关闭该兼容层。

---

## 语言切换
插件界面支持 **English** 与 **简体中文**，在三个选项之间选择：

| 选项 | 含义 |
| --- | --- |
| `自动（跟随系统语言）`（Auto） | 依据 `Application.systemLanguage` 选择：简体中文系统使用简体中文，其余一律使用英文 |
| `English` | 始终英文 |
| `简体中文` | 始终简体中文 |

- **在哪里切换**：`工具 > 部件装配器 > 部件编辑`（英文路径 `Tools > Avatar Part Assembler > Part Authoring`
  仍然可用）窗口工具栏右端的语言下拉框；`AvatarPartInstaller` 检查器顶部也有同一个选择器。两处共用同一个
  全局设置，切换后立即重绘所有已打开的编辑器窗口与场景视图，无需重启编辑器。
- **默认值**：未保存过选择时为 **English**。这样既保证升级本插件的现有用户看到的界面与诊断不变，也让
  英文成为永远不会缺失的兜底语言。选择一次后会被记住，包括选择“自动”。
- **保存位置**：选择保存在编辑器偏好（`EditorPrefs`）的键
  `dev.avatar-part-assembler/localization/language` 下，属于**当前用户**的编辑器状态，不会写入项目资产、
  场景或配置文件，因此不会弄脏版本库。
- **术语**：部件、目标渲染器、部件根对象、移除区域、接缝、材质语义、UV 语义、合并骨架、冲突优先级、
  配置文件。中英文对照见文末[术语表](#术语表)。
- **刻意保留英文的内容**：`APAxxx` 错误码与 `UV_SEMANTIC_CHANNEL_ABSENT` 这类稳定助记标题、`reason=...`
  这类稳定原因标记、验证消息正文、异常文本、资产名与层级路径。它们是可搜索、可测试的契约而不是界面文案；
  中文界面会在错误码旁追加一句中文简短说明，因此信息不会丢失。例如：

  ```text
  错误 APA050 UV_SEMANTIC_CHANNEL_ABSENT（UV 语义声明的通道在部件网格上不存在） [3]: ... :: reason=channel-absent
  ```

  在中文界面看到英文句子是**有意的**：把它翻译掉会让中文用户提交的缺陷报告与英文用户的无法对齐。

- **覆盖面**：M10 新增或改动的每个标签、帮助文本、警告与错误都有简体中文条目——两个骨架选择框、`推断骨架`、
  接缝容差与配对数、按世界坐标生成接缝、折叠后的地址列表，以及 `APA042`/`APA043`/`APA044` 各一句说明；
  界面不会把英文枚举成员名直接显示给用户。尚未翻译的新字符串按英文兜底显示，而不是显示为空。

---

## 两种工作流

### 作为角色用户（安装部件）

1. 把部件预制体下载或复制到工程中。
2. 把预制体拖到角色下。
3. 完成。预制体上的 `AvatarPartInstaller` 声明它替换什么，构建与场景视图预览都会安装它；你不需要手动
   配置 Modular Avatar。
4. 卸载：删除该预制体。角色回到此前完全相同的状态。

部件**不是**以破坏方式安装的：安装器只在 NDMF 为预览和上传构建的临时副本上生效，你的制作场景永远不会被
修改。

### 作为部件作者（制作部件）

打开 `工具 > 部件装配器 > 部件编辑`，窗口会引导整个流程：

1. **选择**：指定角色根对象，再指定目标身体渲染器。
2. **部件**：指定部件根对象与部件渲染器。
3. **两个骨架**：选择 **目标骨架**（位于角色根对象之下、拥有身体骨骼的那一级）与 **部件骨架**（位于部件根
   对象之下、拥有部件骨骼的那一级）。`推断骨架` 依据层级给出建议，但它只是建议——两个选择必须由你确认。
   骨骼的身份是它**相对于自身所选骨架**的路径，因此两个骨架必须是身体与部件相互对应的骨骼层级；层级选错
   不会报错，但会让本该合并的骨骼各成一根新骨骼。旧版的合并路径、前缀、后缀与名称推断控件已移除。
4. **接缝**：设定以**世界单位**表示的容差，按 `按世界坐标生成接缝` 一次性生成成对索引，并检查配对数与
   预览。接缝不再手工逐点拾取，也不再填写两个索引列表。
5. **兼容性**：按 `捕获签名` 记录目标身体的签名（可用时的网格 GUID、顶点数、各子网格索引数量与拓扑、
   形态键名称与帧数、骨骼路径）。此后所有与拓扑相关的数据只对那一个网格有效。如果你跳过这一步直接执行
   后续动作，窗口会在 `验证`、`试运行装配`、`保存配置文件资产`、`创建部件预制体` 与
   `更新预制体上的安装器` 之前自动捕获一次——前提是配置文件**从未**捕获过签名且当前目标可用；已经捕获
   过的签名永远不会被覆盖。
6. **移除区域**：声明部件要替换的基础三角形。三种工具写入同一个集合：`在场景中拾取三角形` 会启用场景视图
   工具，逐个拾取；**纹理遮罩** 区块把一张黑白纹理一次性转换成整片区域（白色选中、黑色保留；每个三角形取
   7 个采样点，其中至少 4 个通过才选中；纹理只是编辑阶段的输入，不会被保存）；`移除该子网格内全部`、
   `添加地址`、`添加列表` 是键盘/数值替代方式。地址列表折叠为 `地址（N）`，展开后最多绘制 28 行，因此
   几千个地址不会淹没窗口；数字地址输入与遮罩的应用方式始终可以精确修正整个集合。
7. **UV 语义**：声明部件携带的 `(语义名称 → 来源通道)` 对。`从部件网格推断` 会填充这些行；
   `APA050` 会拒绝网格实际并不具备的通道声明。
8. **材质语义**：声明 `(语义名称 → 来源子网格 → 材质 → 策略)` 行，`从材质推断` 会填充它们。材质**资产名**
   永远不会被当作语义。
9. **骨骼与形态键**：设置 `合并骨架`（在所选部件骨架上生成一个指向所选目标骨架的临时 Modular Avatar
   合并配置，不写前缀/后缀、不做推断）与 `允许仅部件形态键`。
10. **验证**：运行真正的验证器；`试运行装配` 只做规划、不创建网格，用于确认部件是否真的能构建。
11. **`保存配置文件资产`**：写出 `ApaPartProfile`（架构版本 4）。只有明确确认后才会替换同路径的已有资产。
12. **`创建部件预制体`**：生成可移植的预制体并添加/更新其 `AvatarPartInstaller`；
    `更新预制体上的安装器` 则把已有预制体重新指向当前配置文件。

`AvatarPartInstaller` 检查器显示构建实际使用的信息——部件标识、槽位、架构版本、签名状态、目标路径、
移除/接缝/语义数量、解析到的目标——并提供 `验证`、`创建配置文件资产…`、`打开配置文件`、
`在部件制作窗口中编辑` 等快捷操作。

制作流程中，任何写入都先通过 `ApaAuthoringValidation`；没有明确确认不会替换已有资产；所有触及作者数据的
编辑都经过 `Undo`。

---

## 骨架选择与骨骼身份

M10 之前，部件骨骼的“身份”是它从角色根对象到自身的完整路径；那条路径取决于部件恰好被放在哪里，是摆放
细节而不是关节自身的性质。现在身份的基准是两个由你显式选择的骨架：

| 选择 | 路径相对谁记录 | 归一化取值 | 约束 |
| --- | --- | --- | --- |
| 目标骨架 | 角色根对象 | `ApaAvatarPath.Root`（`.`）表示角色根对象本身 | 必须是角色根对象本身或其后代 |
| 部件骨架 | 部件根对象 | `.` 表示部件根对象本身 | 必须是部件根对象本身或其后代 |

- 构建时先解析两个根，再把每个渲染器的骨骼记录为**相对于它自己那一侧所选骨架**的路径；两条相对路径逐字节
  相同时，它们就是同一根关节并合并。骨架根自身记录为 `.`。角色根对象到部件骨骼的完整外层路径从此不再参与
  身份判断。
- 因此两个选择必须是**相互对应的骨骼层级**：目标骨架选到身体骨架的哪一级，部件骨架就要选到部件骨架的同一
  级。位置与名字都只是猜测，层级对应则是作者给出的声明——这正是用两个选择取代名称启发式的原因。
- 同一个身体上的多个安装器必须选择**同一个**目标骨架，否则以
  `APA043 reason=conflicting-target-armature-paths` 阻止。
- 构建时生成的 Modular Avatar 合并组件挂在**所选部件骨架**上、指向**所选目标骨架**，并始终写入空前缀、
  空后缀、不做推断。于是 Modular Avatar 的“名称完全相同才合并”与骨架相对身份描述的是同一个关系。
- 旧版的 `MergeTargetPath`、`MergePrefix`、`MergeSuffix`、`InferMergeNames` 仍存在于资产中并可往返序列化，
  但构建不再读取其中任何一个，窗口也不再显示这些控件。它们保留只是为了旧资产还能读入，而不是一条备用
  路径。

### 骨架相关的阻断诊断

| 码 | 触发条件 | 稳定的 `reason=` 标记 |
| --- | --- | --- |
| `APA043 ARMATURE_SELECTION_INVALID` | 未选择、路径无法解析，或所选对象不在它必须属于的根下；合并配置无处可放时也用它 | `missing-target-armature`、`missing-part-armature`、`target-armature-not-found`、`part-armature-not-found`、`target-armature-outside-root`、`part-armature-outside-root`、`conflicting-target-armature-paths`、`null-target-armature`、`null-part-armature`、`part-top-bone-outside-armature`、`target-armature-inside-part-armature`、`merge-target-is-part` |
| `APA044 BONE_OUTSIDE_SELECTED_ARMATURE` | 某根**携带权重**的骨骼不在其渲染器所选骨架内 | `reason=bone-outside-armature` |

只检查**真正携带权重**的骨骼：没有任何顶点以高于阈值的权重引用过的骨骼槽（包括 `null` 槽）不参与身份要求。
加权骨骼落在所选骨架之外则是实打实的缺陷——它的路径无法相对骨架记录，部件骨骼便永远无法与它所属的身体
骨骼合并，权重会跟随一根被追加出来的重复骨骼。

---

## 创建配置文件（Part Profile）

配置文件（`ApaPartProfile`）是**部件自描述**的序列化资产：它随预制体一起分发，内含部件标识与稳定部件 ID、
兼容性签名、移除三角形集合、显式成对的接缝、UV 与材质语义、两个骨架选择、骨骼与形态键策略、冲突优先级
与槽位模式。

要点：

- 架构版本为 **4**。版本 2 与版本 3 的配置文件被**按无操作接受**（新增字段的默认值复现旧行为，读取时不会
  改写资产）；版本 1 被拒绝，必须重新制作。
- **版本 3 的配置文件能读入，但不能直接构建。** 它没有两个骨架选择，接缝也是没有配对关系的无序集合，
  因此会在构建时以 `APA043`（缺骨架选择）与 `APA042`（缺接缝配对）阻止，直到你在窗口里重新制作一次：
  选择两个骨架、按世界坐标重新生成接缝。这不是迁移失败，而是缺少必须由作者声明的信息——工具不会替你猜。
- 签名是拓扑相关数据的唯一护栏：网格 GUID（溯源用）→ 顶点数 → 各子网格索引数量与拓扑 → 形态键名称与
  帧数 → 骨骼路径签名。护栏只**校验并阻止**，绝不会为了“让它通过”而改写配置文件。
- 签名缺少必需校验字段时以 `APA024` 阻止，与 `APA012`（目标已是另一个网格）区分开，因为两者的修复方式
  不同。
- 写出的配置文件必须可被规划：`保存配置文件资产` 之前会执行完整验证与一次试运行装配，因此一个“验证通过
  但永远无法构建”的配置文件不会被写出。唯一的例外在设计上已被消除：最终材质槽位覆盖的判定属于规划器。

---

## 创建部件预制体

`创建部件预制体` 从场景中的部件根对象生成预制体，并在预制体根对象上添加/更新 `AvatarPartInstaller`。
可移植性是这一步的全部意义：

- 安装器的配置文件必须是项目资产，否则拒绝；
- 部件根对象是预制体**内部**的引用，天然可移植；
- 安装器的显式目标渲染器对象**绝不**指向制作场景中的身体渲染器：目标以配置文件中捕获的渲染器路径随包
  分发，指向预制体外部的赋值会被清除并给出说明；
- 保存前会扫描层级中的每一个序列化引用，发现无法存入资产的引用（或扫描在预算内无法证明没有）时拒绝写入；
- 路径被其它类型资产占用时拒绝写入——允许替换预制体不等于允许删除一个材质。

`更新预制体上的安装器` 只改写安装器自身的字段，不会覆盖预制体里的几何体，因此预制体侧的修改不会丢失。

---

## 预览

预览是挂在**同一个** `Transforming` 处理阶段上的 NDMF 渲染过滤器：

```text
seq.Run(ApaAssemblyPass.Instance).PreviewingWith(ApaPreviewRegistration.CreateFilter())
```

- 每个目标分组一个代理渲染器；节点把装配好的网格、材质、骨骼与形态键权重写到管线交给它的代理上。
  原始渲染器的网格、材质、层级与配置文件资产永远不会被写入。
- **阻止即缺席**：当前输入非法时，过滤器不为该渲染器返回任何分组，因此 NDMF 没有代理，原始身体照常渲染，
  上一次“看起来成功”的预览不会在非法编辑后残留。原因会作为诊断报告。
- 缓存键是内容**指纹**（`apa-preview-fnv1a64-v1`，FNV-1a，覆盖捕获输入、逐属性的网格数据、配置文件序列化、
  变换、材质标识与数值容差），不使用进程随机化哈希；失败**不**缓存。
- 网格缓存有界（默认容量 4，加上活动租约），被淘汰的网格立即销毁；域重载前、退出时与播放模式切换前会整体
  销毁。
- 预览开关出现在 NDMF 的 *Configure Previews* 中：
  `dev.avatar-part-assembler/preview/Main`（默认开）与
  `dev.avatar-part-assembler/preview/DebugOverlay`（默认关，名字与标题随语言设置显示）。
- **调试叠加层**把被移除的基础三角形画成红色、把接缝对应关系画成两个作者位置之间的黄色连线——连线长度
  就是匹配误差，完美焊接会画成一个十字。它有上限（每类最多 4000 个图元、最多 8 行标签），不写入任何对象，
  并且只读取捕获的快照与不可变规划。

---

## 构建

```text
Generating     ApaMergeArmaturePass  「创建临时合并骨架配置」
               - 为每个需要合并的部件创建一个临时 ModularAvatarMergeArmature
               - 记录每个配置与合并必须搬移的骨骼

Transforming   （nadena.dev.modular-avatar 在此运行）
               ApaAssemblyPass       「将部件几何体装配到目标身体网格」
               - 声明 AfterPlugin("nadena.dev.modular-avatar")
               - 构建已失败时立即返回
               - 角色没有生效的安装器时立即返回
               - 先验证合并后置条件，再开始处理
```

四个刻意的性质：

- **合并是被验证的，不是被假设的。** Modular Avatar 销毁一个它*跳过*的配置与销毁一个已合并的配置没有区别，
  因此组件消失只是一半的检查；该阶段还要求部件的顶层骨骼已经离开部件根对象。否则构建以
  `reason=merge-not-applied` 阻止（配置仍然存活时是 `reason=merge-not-consumed`，意味着 Modular Avatar 的
  合并阶段根本没有运行）。
- **已经失败的构建不会被改写。** `BuildContext.Successful` 为 false 是唯一的闸门。
- **没有生效安装器的角色不会被触碰，也不会被报告。** 闸门与处理器使用同一套安装器发现逻辑，因此本包不会
  把无关角色的构建变红。
- **顺序是声明的，不是巧合。** `AfterPlugin` 使用限定名（`"nadena.dev.modular-avatar"`），因为 Modular Avatar
  的插件类是它自己程序集内的 internal 类型。

临时合并配置挂在**所选部件骨架**上并指向**所选目标骨架**，且始终写入空前缀、空后缀、不做推断：Modular
Avatar 的“名称完全相同才合并”因此恰好复现骨架相对身份所描述的关系，而不是在它之外再引入一套名称启发式。

两个阶段都默认只对 VRChat 角色运行。

### 两个工作流共享同一个处理器

预览与构建走同一条“捕获 → 验证 → 规划 → 构建”路径，只**规划一次**、只**构建一次**：一个分组存在阻断问题
时整个角色构建失败，不会返回任何网格，因此不存在“装配了一半”的角色。

---

## 策略总览

### 目标分组与冲突优先级

| 情况 | 结果 |
| --- | --- |
| 一个目标渲染器 | 一个分组、一份规划、一个网格 |
| 两个渲染器，分别被不同安装器显式指定 | 两个分组；分组键是渲染器相对角色根对象的路径 |
| 同一分组内两个安装器指定了不同渲染器 | 阻断 `APA006 reason=conflicting-target-renderers` |
| 分组 A 合法、分组 B 非法 | **整个角色失败**；分组相关问题在 detail 中带 `group=<路径>` |
| 完全没有生效的安装器 | 不适用：不验证、不修改、不报告 |

`ApaPartIdentity.ConflictPriority` 序列化在配置文件上（随预制体分发），0 表示未声明且完全惰性：

| 情况 | 结果 |
| --- | --- |
| 分组内没有任何部件声明优先级 | 顺序与引入优先级之前完全一致 |
| 移除区域重叠，每个声明方都声明了优先级且最高值唯一 | 成功；**警告 `APA035 reason=removal-overlap-resolved-by-priority`** 指明归属方、其优先级、落败方与有界采样。无论哪种情况移除集合都是并集，优先级只决定区域的*归属* |
| 移除区域重叠，至少一方未声明（0） | 阻断 `APA010 reason=undeclared-priority` |
| 移除区域重叠，最高优先级相同 | 阻断 `APA010 reason=priority-tie` |
| 负数优先级 | 阻断 `APA037 reason=negative-conflict-priority` |

### 槽位与移除区域

| # | 情况 | 结果 |
| --- | --- | --- |
| S1 | `自定义` 槽位 | 任意数量部件，无冲突 |
| S2 | 非自定义槽位，恰好一个 `替换` | 通过 |
| S3 | 非自定义槽位，两个及以上 `替换` | 阻断 `APA013 DUPLICATE_PART_SLOT reason=duplicate-replace-slot` |
| S4 | 非自定义槽位，一个 `替换` + N 个 `附加` | 通过 |
| S5 | 非自定义槽位，N 个 `附加`，没有 `替换` | 通过 |
| S6 | `附加` 部件声明了非空移除区域 | 阻断 `APA013 reason=augment-declares-removal` |
| S7 | 槽位取值不在定义列表中 | 阻断 `APA023 INVALID_PART_SLOT` |
| R1 | 地址在其子网格范围内 | 只移除该三角形 |
| R2 | 非负三角形索引越界 | 阻断 `APA017 REMOVAL_INDEX_OUT_OF_RANGE` |
| R3 | 地址本身畸形（负子网格/负三角形/非三角形子网格） | 阻断 `APA026 INVALID_TRIANGLE_ADDRESS` |
| R4 | 接缝顶点所在的三角形全部被移除 | 接缝顶点**保留**：它是焊接目标，不是可移除几何体 |
| R5 | 黑白遮罩无法转换成三角形选择 | 阻断 `APA041 REMOVAL_MASK_TEXTURE_FAILED`，`detail` 中带稳定的 `reason=...` 标记 |

#### 黑白遮罩选择（纹理遮罩）

逐个点击三角形无法应付几千个三角形的区域。移除区域中的 **纹理遮罩** 区块通过目标网格的 UV，把一张黑白纹理
转换成移除集合：

| 设置 | 含义 |
| --- | --- |
| 遮罩纹理 | 一张 `Texture2D`，通过目标网格的 UV 采样 |
| UV 通道 | 0–7，默认 0 —— 绘制遮罩时所对应的通道 |
| 阈值 | 0–1，默认 0.5 —— 采样点亮度达到或超过该值即视为选中 |
| 反相 | 交换白色与黑色的含义 |
| 应用方式 | 替换选择（默认）、添加到选择、从选择中减去 |

**颜色含义。** 未勾选“反相”时，**白色选中要移除的三角形，黑色保留它们**——也就是“把想删掉的区域涂白”这一
直觉读法；勾选“反相”则相反。采样点的亮度按 **RGB 亮度** 计算（`0.299 R + 0.587 G + 0.114 B`，Rec.601，
直接作用于纹理存储的 8 位数值）；**忽略 alpha**，因此全透明的白点与不透明的白点会选中同样的三角形。

**采样规则固定，不可配置。** 每个三角形列表子网格中的每个三角形都取 **7 个采样点**——3 个顶点的 UV、3 条边
的中点、以及重心——当 **7 个采样点中至少有 4 个**达到或超过阈值时选中该三角形。多数表决把遮罩边界的分辨率
控制在约四分之一个三角形，也不会被某一个恰好落在别处的角点左右。除此之外没有任何可调项，因此同一张图
永远选中同一批三角形。采样遵循遮罩自身的导入设置：按纹理的过滤模式选择双线性或最近点；采样位置按硬件
的方式从 UV 推导（纹素中心位于 `(i + 0.5) / size`），再由纹理的环绕模式对**整数纹素索引**寻址——repeat
把索引绕到另一端，因此 `u = 0` 处的采样会在最后一个与第一个纹素之间插值；mirror 以两倍周期反射索引；
clamp 与 `MirrorOnce` 停在边界纹素上。

**不需要勾选 Read/Write Enabled，也不改动导入设置。** 工具从不要求作者修改遮罩的导入设置，也从不写入这些
设置。读取遮罩只有一条路径：把纹理 blit 到临时渲染纹理再读回，这正是让已导入、不可读或块压缩的遮罩无需
改动资产即可解码的方式。这里**刻意没有** `CopyTexture` 这条设备端拷贝捷径——它只更新 GPU 侧的副本，不更新
像素读取所返回的 CPU 缓冲，可能悄悄给出长度正确但全黑的遮罩——并且**不会**在尝试之前按像素格式拒绝：
某个来源如果设备确实无法渲染并读回，会以 `reason=texture-readback-failed` 报告，而不是预先拒绝该格式。
无论转换成功还是拒绝，都会释放所有临时对象，因此失败不会留下任何残留。遮罩纹理与网格始终只被读取。

**只保存三角形集合。** 纹理是**编辑阶段的输入**，不是构建依赖：它永远不会被存入 `ApaPartProfile`，不会被
预制体引用，重建部件也不需要它。转换产出的是与场景视图拾取完全相同的规范 `RemovedTriangleAddress` 集合，
由 `ApaRemovalMask` 规范化后写入配置文件（子网格索引 + 该子网格内的三角形索引）。之后移动、删除纹理或改变
其导入设置，都不会影响任何已经制作好的部件。

**反馈与另外两种工具。** 遮罩无法运行时 `应用遮罩` 处于禁用状态——未选择目标网格、未指定遮罩纹理、目标网格
不可读，或网格没有所选 UV 通道——这条原因就显示在按钮旁边，因此禁用状态总能说明该修什么。状态行报告选中
数量以及转换考虑过的三角形数量；**空结果也是合法结果**（在“替换选择”模式下它会按作者的明确点击清空选择，
这正是“这张遮罩不定义任何区域”的表达方式），不会被当成错误。无法运行的转换——网格不可读、UV 通道不存在或
长度不符、阈值非有限或越界、拓扑不受支持、设备拒绝读回——都会以 **阻断级
`APA041 REMOVAL_MASK_TEXTURE_FAILED`** 停止；完整诊断（错误码、助记标题、消息与稳定的 `reason=` 标记）会
显示在按下 `应用遮罩` 的遮罩区块中，并再次出现在写入区的“上次写入报告”列表里，同时保持已有选择不变。场景
视图拾取与数值地址列表始终可用：遮罩负责大片区域，拾取负责它弄错的那两个三角形，地址列表负责精确可复现
的录入。

一次 `应用遮罩` 对应**一条 Undo 记录**：无论哪种应用方式，一次应用一次撤销。

#### 移除地址列表（折叠）

一个真实的移除区域可以有几千个三角形地址，把它们全部画出来会把窗口变成一张表格。列表因此折叠为
`地址（N）`：展开后最多绘制 **28 行**，每一行仍可单独移除，其余数量以摘要行说明（摘要也会说明数字地址
输入与遮罩模式可以编辑整个集合）。与其它列表共用的全局 `MaxListedRows`（200）**刻意保持不变**——它是
其它列表的显示上限，降低它只会让别处也丢信息；列表的用途是核对与单点修正，批量修正是数字地址输入与
纹理遮罩的 Replace / Add To Selection / Subtract From Selection 三种应用方式的职责。

### 接缝

接缝由**一次动作**从世界位置生成，并以**显式配对**保存在配置文件里：两个网格都按**静置姿势**读取
（`sharedMesh.vertices` 经 `transform.localToWorldMatrix` 变换，绝不使用 `BakeMesh` 的当前动画姿势），
一个均匀空间哈希在**世界单位**容差内找出所有世界坐标重合的顶点对。

| 性质 | 规则 |
| --- | --- |
| 容差 | 默认 `1e-4`（0.1 mm），窗口接受 `1e-7` 到 `1e-2`；用世界单位，因为接缝就是按世界坐标制作的 |
| 配对 | `Base.VertexIndices[i]` 与 `Part.VertexIndices[i]` 是一处焊接；结果本身就是配对，不再有“两侧无序集合按位置猜配对”这一步 |
| 确定性 | 部件顶点按索引升序访问，每个取最近的、尚未被占用的目标顶点；距离相同时取索引较小的目标顶点 |
| 一对一 | 同一个目标顶点不会被占用两次（`APA003 reason=duplicate-base-claim`） |
| 无匹配 | 任何目标顶点附近都没有部件顶点时，由生成器报告 `APA002 reason=no-world-coincident-vertices`，而不是在构建时搜索 |

构建阶段只**消费**这些配对：`SeamResolver` 校验索引集合、两侧数量、配对位置的有限性与基础侧的一对一
占用，不再做任何位置搜索。原因是一个“角色根对象局部空间”的容差在每一个被缩放的层级上都是不同的世界
距离，而两个顶点靠得很近时，位置搜索只是在猜。

配对带有版本：`PairingVersion` 为 `0` 表示 M10 之前的无序集合，为 `1` 表示显式配对。M10 之前写出的
配置文件保持 `0`，并以新的 **阻断级 `APA042 SEAM_PAIRING_REQUIRED`（`reason=seam-pairing-required`）**
被拒绝，直到你重新生成一次接缝。窗口显示容差、配对数与一小段预览，并提供 `清除` 与 `重新生成`，不再逐项
列出成百上千个顶点。

手工拾取两侧接缝顶点、两个索引列表文本框与两个场景视图拾取模式都已移除：它们要求作者在两个不同的空间里
维持同一份一一对应关系，而这份关系正是工具可以一次性算准的。

焊接时保留的**基础**顶点拥有位置、法线、切线、颜色与蒙皮权重；UV 是唯一例外（同名语义在容差内必须一致，
否则 `APA004`；仅一侧存在时使用该侧的值）。

### UV 与材质

| 情况 | 结果 |
| --- | --- |
| 同一语义出现在不同来源通道 | 合并为一个最终通道；通道索引不是身份 |
| 同一语义在焊接顶点上取值不一致 | 阻断 `APA004 SEAM_UV_MISMATCH` |
| 多个部件为同一仅部件语义在同一焊接顶点写入不同值 | 阻断 `APA025 WELD_UV_CONFLICT`，**没有优先级逃生通道** |
| 存在但未被声明（也未隐式命名）的来源通道 | 阻断 `APA036 reason=undeclared-present-channel` |
| 最终通道超过 8 个 | 阻断 `APA005 UV_CHANNEL_OVERFLOW` |

材质策略 `自动` / `使用目标材质` / `保留部件材质` / `强制新建槽位` 按语义生效；基础侧声明该语义时，基础槽位
就是**锚点**。基础渲染器自身的材质永远不会被修改或替换，只会被引用。来源子网格没有产出任何最终材质槽位时
阻断 `APA038 SUBMESH_WITHOUT_MATERIAL_SLOT`（与 `APA009` 是两个不同条件，因此是两个不同的码）。

### 形态键与骨骼

| 情况 | 结果 |
| --- | --- |
| 仅存在于基础侧 | 在保留的基础顶点上存活；被移除顶点上的增量随之消失 |
| 仅存在于部件侧，且所有接缝增量恰好为零 | 接受（除非配置把 `允许仅部件形态键` 设为 false） |
| 仅存在于部件侧，接缝增量非零 | 阻断 `APA029 reason=part-only-seam-delta` |
| 仅存在于部件侧且策略不允许 | 阻断 `APA040 PART_ONLY_SHAPE_DISALLOWED` |
| 基础与部件同名 | 合并；帧数与每一帧权重必须**完全**相等，否则 `APA028`；接缝增量在目标渲染器局部空间中必须一致，否则 `APA029` |
| 同一网格内重名 | 阻断 `APA027 BLENDSHAPE_DUPLICATE_NAME` |
| 最终骨骼表 | 先按身体自身顺序排身体骨骼（含只有部件加权、但身体签名已声明的路径），再按部件规范顺序与来源顺序排新增的部件骨骼 |
| 部件骨骼与身体骨骼在各自所选骨架下的相对路径逐字节相同 | 视为同一根关节：部件骨骼**重定向到身体骨骼**。身体的 bind pose、世界变换与最终 Transform 权威，部件自身 bind 变换被丢弃，不再比较；每个部件输出一条 `APA007 reason=part-bone-remapped-to-target` 汇总 |
| 身体签名**声明**了某路径，但没有任何身体顶点给它加权，而部件给它加权 | 该骨骼仍归身体所有：在任何部件处理之前，就用身体自己的变换、owner 与来源骨骼索引建立该条目，部件重定向到它。身体声明的路径就是同一根关节 |
| 身体内两根骨骼共用一条路径，且有部件给该路径加权 | 阻断 `APA008 reason=duplicate-bone-identity`：没有唯一的身体骨骼可供权重跟随 |
| 身体内两根骨骼共用一条路径，但没有任何权重到达它 | **不阻断**，也不进入最终骨骼表——仍适用“未被引用的槽不是缺陷” |
| 携带权重的骨骼没有稳定身份（空路径） | 阻断 `APA008 reason=bone-without-identity` |
| 携带权重的骨骼不在其渲染器所选骨架内 | 阻断 `APA044 BONE_OUTSIDE_SELECTED_ARMATURE reason=bone-outside-armature` |
| 未被任何顶点以高于阈值的权重引用过的骨骼槽（包括 `null` 槽） | **不阻断**，也不进入最终骨骼表；它的重映射项保持 `-1` |
| 权重数组与顶点数不符、负权重、权重和为零 | 阻断 `APA031 INVALID_BONE_WEIGHT` |
| 顶点整套权重都不高于权重阈值 | 阻断 `APA031 reason=zero-weight-sum` |

权重阈值是 `ApaNumericPolicy.WeightEpsilon`（新策略值，默认 `1e-5`），同一阈值同时决定“哪些骨骼需要身份”
与“哪些影响会在重映射后存活”：不高于阈值的权重被清零为索引 0、权重 0，因此“来源有效”与“重映射能解析
每一个存活的影响”不可能互相矛盾。未被引用的骨骼槽（包括 `null`）不再需要身份——M10 之前，一个空空如也的
槽会让整个部件以 `APA008` 阻止，而它其实不影响任何一个顶点。

**变换不同从来不是歧义。** 自 M11 起，身体对其签名声明的每一条路径都权威：相对路径逐字节相同即同一根关节，
部件自身的 bind 变换被丢弃而不是被比较，因此在场景别处摆放的部件不再阻断，也不再追加重复骨骼。同一条规则
覆盖“没有任何身体顶点加权”的路径：该条目在任何部件处理之前就用身体自己的变换建立，因此预览与 NDMF 构建把
同一个身份解析到同一个 Transform。`APA008` 只保留给真正的歧义：携带权重却无身份的骨骼、同一来源内两根
携带权重的骨骼共用一条路径、以及身体内两根骨骼共用一条被部件加权的路径。

---

## 诊断

稳定的、有文档的、可搜索的错误码。**错误码的含义永不改变，退役的错误码永不复用于其它条件。**
`ApaErrorCode` 是**整个包（含制作层）唯一的分配表**：它声明每个码、渲染每个助记标题并维护分配记录
（`ApaReservedCodes`）。严重级别决定行为：`Error` 阻止预览与构建，`Warning` 与 `Info` 不阻止。

```text
APA001 SEAM_VERTEX_COUNT_MISMATCH        APA023 INVALID_PART_SLOT
APA002 SEAM_POSITION_MISMATCH            APA024 INCOMPLETE_COMPATIBILITY_SIGNATURE
APA003 SEAM_DUPLICATE_POSITION_MATCH     APA025 WELD_UV_CONFLICT
APA004 SEAM_UV_MISMATCH                  APA026 INVALID_TRIANGLE_ADDRESS
APA005 UV_CHANNEL_OVERFLOW               APA027 BLENDSHAPE_DUPLICATE_NAME
APA006 TARGET_RENDERER_NOT_FOUND         APA028 BLENDSHAPE_FRAME_MISMATCH
APA007 TARGET_BONE_NOT_FOUND             APA029 BLENDSHAPE_SEAM_DELTA_MISMATCH
APA008 BONE_HIERARCHY_CONFLICT           APA030 INVALID_BLENDSHAPE_DELTA
APA009 MATERIAL_SEMANTIC_CONFLICT        APA031 INVALID_BONE_WEIGHT
APA010 REMOVAL_REGION_OVERLAP            APA032 INVALID_SPACE_TRANSFORM
APA011 INVALID_BINDPOSE                  APA033 INVALID_AUTHORING_PATH
APA012 PART_PROFILE_INCOMPATIBLE         APA034 NON_PERSISTENT_REFERENCE
APA013 DUPLICATE_PART_SLOT               APA035 REMOVAL_OVERLAP_RESOLVED_BY_PRIORITY（警告）
APA014 UNSUPPORTED_MESH_ATTRIBUTE        APA036 UNDECLARED_UV_CHANNEL
APA015 UNKNOWN_PROFILE_SCHEMA            APA037 INVALID_CONFLICT_PRIORITY
APA016 NON_FINITE_VALUE                  APA038 SUBMESH_WITHOUT_MATERIAL_SLOT
APA017 REMOVAL_INDEX_OUT_OF_RANGE        APA039 INACTIVE_INSTALLER_SKIPPED（信息）
APA018 INVALID_SEAM_SELECTION            APA040 PART_ONLY_SHAPE_DISALLOWED
APA019 DUPLICATE_SEMANTIC                APA041 REMOVAL_MASK_TEXTURE_FAILED
APA020 INVALID_SEMANTIC_NAME             APA050 UV_SEMANTIC_CHANNEL_ABSENT
APA021 DEGENERATE_OUTPUT_TRIANGLE        APA999 INTERNAL_ERROR
APA022 INVALID_EPSILON                   APA042 SEAM_PAIRING_REQUIRED
APA043 ARMATURE_SELECTION_INVALID        APA044 BONE_OUTSIDE_SELECTED_ARMATURE
```

`APA041` 由 M9 分配，只在制作层产生，用于“黑白遮罩无法转换成三角形选择”；具体条件由 `detail` 中稳定的
`reason=...` 标记区分（缺少目标网格、网格不可读、缺少遮罩纹理、纹理类型/尺寸不受支持、设备拒绝渲染并读回、
UV 通道越界或不存在、通道长度与顶点数不符、阈值非有限或越界、子网格拓扑不受支持、顶点索引越界、UV 非有限）。
像素格式**不是**拒绝条件：压缩、crunched 或不可读的纹理正是渲染读回路径要处理的输入，只有设备自己无法渲染
并读回时才会得到 `reason=texture-readback-failed`（没有 `reason=unsupported-texture-format`）。

`APA042`、`APA043` 与 `APA044` 由 M10 分配，属于**核心码**，登记在 `ApaReservedCodes.Milestone10` 并可由
`IsMilestone10Code` 判定：

- `APA042 SEAM_PAIRING_REQUIRED` —— 接缝还是 M10 之前的无序集合，`reason=seam-pairing-required`；补救是
  在窗口中重新生成一次接缝，而不是迁移：把两个无序集合按位置重新配对正是可能焊错顶点的猜测。
- `APA043 ARMATURE_SELECTION_INVALID` —— 骨架选择缺失、路径无法解析，或所选对象不在它必须属于的根下；
  具体条件由 `reason=` 标记区分。
- `APA044 BONE_OUTSIDE_SELECTED_ARMATURE` —— 一根携带权重的骨骼不在其渲染器所选骨架内，
  `reason=bone-outside-armature`。

`APA045` 与更大的编号是下一个可用错误码。M9 的 `APA041` 仍是唯一的遮罩拒绝码，遮罩的采样规则
（7 个采样点、至少 4 个、RGB 亮度、忽略 alpha、唯一读回路径、不按格式拒绝、按整数纹素索引寻址）没有改变。
诊断按确定顺序排序并去重，因此相同输入产生相同报告——即使验证器与规划器独立发现了同一缺陷。

中文界面下每条诊断都会在稳定错误码与英文助记标题之后追加一句中文简短说明；`APAxxx`、助记标题与
`reason=...` 原样保留。

---

## 已知限制与诚实的缺口

这些是里程碑边界与刻意的产品决定，而不是疏忽；每一条都由诊断而非沉默来体现。

### 未实现

| 未实现 | 诊断 / 行为 |
| --- | --- |
| 非三角形子网格拓扑（点、线、非三角形列表） | `APA014 UNSUPPORTED_MESH_ATTRIBUTE` |
| 多个部件**替换**同一个非自定义槽位 | `APA013`；请改用 `附加` |
| UV 或形态键冲突的优先级解析 | `APA025` / `APA028` / `APA029` 阻断。这是永久设计：一个焊接顶点只持有一个值，而帧索引是位置而非身份 |
| 自动重拓扑、顶点数不同时的接缝桥接、自动 UV 接缝修复、同名材质冲突的自动消解、从骨骼权重猜测移除区域、修复损坏的骨骼层级 | 只报告，绝不猜测——产品决定 |
| 自动选择或推断两个骨架 | 不是：`推断骨架` 只依据层级给出建议，两个选择必须由作者确认。层级对应关系是声明，不是可以推算出来的事实 |
| 把 M10 之前的无序接缝集合自动迁移为配对 | 不是：以 `APA042` 拒绝并要求重新生成一次。把两个无序集合按位置重新配对，正是可能把接缝焊到无关顶点上的猜测 |
| 界面与消息的英文之外语言 | **已提供**：English 与简体中文（M8），英文仍是不可缺失的兜底 |

### 不是代码缺陷的缺口与风险

| 缺口 | 为什么重要 | 检查位置 |
| --- | --- | --- |
| 从未在 Unity 中运行 | 整个行为面都未经验证；编译干净仍可能加载失败，预览也可能与构建不同 | 验收清单第 1–9 组 |
| 预览/构建等价性目前只是设计属性 | 由“一条代码路径 + 一个处理器”保证；只有清单中的对比行能端到端证明 | 清单 4.2 |
| 合并后置条件依赖 Modular Avatar 的运行时行为 | 阶段验证的是“骨骼离开了部件根对象”，它在你的场景中是否以别的方式搬移尚未验证 | 清单 4.14、4.15 |
| 预览错误路径上的网格生命周期 | 释放是引用计数且在主线程；*出错*的管线构建可能不释放节点，因此预览出错时生成的网格可能泄漏 | 清单 3.15、8.2 |
| `EditorOnly` 交互 | NDMF 会在 Modular Avatar 合并前移除 `EditorOnly` 对象，停放在其下的部件可能在装配前丢失几何体 | 清单 4.18 |
| 骨架锁副作用 | `ModularAvatarMergeArmature` 是 `[ExecuteInEditMode]`，其 `OnEnable` 会触及骨架锁状态；临时组件声明 `NotLocked` 并由 Modular Avatar 销毁，但锁任务是否跨生命周期存活是运行时事实 | 清单 4.17 |
| 真实性能未测量 | 接缝匹配在声明的接缝集合上接近 O(n)，但每次刷新的捕获成本是对每个来源做完整属性拷贝 | 清单 8.1–8.4 |
| 生态矩阵未验证 | AAO、lilToon、PhysBone、Contacts、Animator、预制体变体与上传路径都是清单条目，不是结果 | 清单第 9 组 |
| 本地化的运行时观感未验证 | 语言层本身是纯查表，但它在窗口、检查器与场景视图中的显示效果尚未在编辑器中查看 | 清单第 10 组 |
| 遮罩转换的运行时观感未验证 | 采样规则（含环绕模式的纹素寻址）、读写回路径与控件都是确定性的源码逻辑，但只有真正在编辑器里对一张遮罩点一次 `应用遮罩` 才能证明结果与画面一致 | 清单第 11 组 |
| 骨架选择与世界坐标接缝未在编辑器中观察过 | 两个选择、容差与配对结果、`APA042`/`APA043`/`APA044` 的显示与拒绝时机都只有真正运行才能证明；自动捕获签名与“已捕获不覆盖”也只有运行才能区分 | 清单第 12 组 |

---

## 用户验收入口

**一切行为性内容都需要你亲自运行。**

1. 完整计划：[`USER_ACCEPTANCE_CHECKLIST.md`](USER_ACCEPTANCE_CHECKLIST.md)，分为编译、EditMode 测试套件、
   预览、构建、删除与恢复、确定性与可重复性、架构迁移（v2/v3 → v4）、性能与资源、生态、语言与本地化（M8）、
   黑白遮罩选择（M9），以及骨架选择与世界坐标接缝（M10）十二组。
2. 不要把清单第 4 组的“预览结果等于构建结果”一行跳过。它是产品的核心承诺，也是任何源码评审都无法证明的
   唯一性质。
3. 语言相关的验收在第 10 组：切换语言应立即重绘、跨重启保持、不写入项目资产，且英文路径
   `Tools > Avatar Part Assembler > Part Authoring` 始终可用。
4. 遮罩相关的验收在第 11 组：遮罩产出的区域与画面一致、`反相` 取反、遮罩**不需要** Read/Write Enabled 且
   导入设置不变、纹理不会被写进配置文件、一次应用一条 Undo、空结果是合法结果、拒绝原因可操作（`APA041`），
   并且重复运行与域重载后结果完全一致。
5. M10 相关的验收在第 12 组：两个骨架选择及其缺失/越界拒绝（`APA043`）、M10 之前配置文件的 `APA042` 拒绝
   与重新生成接缝后的通过、未被引用的骨骼槽不再以 `APA008` 阻止、首次使用时自动捕获签名而已捕获的签名
   不被覆盖、缩放层级下的世界坐标接缝生成、折叠的地址列表，以及两种界面语言。
6. M11 身体骨骼权威在第 12 组第 12.11 行：部件给“身体已声明但没有身体顶点加权”的路径加权时，预览与构建
   都必须跟随身体的 Transform，每个部件只输出一条 `reason=part-bone-remapped-to-target` 信息，被部件加权的
   重复身体路径以 `APA008` 阻断，而未被引用的重复槽仍然不阻断。

---

## 仓库布局与程序集依赖方向

```text
Runtime/                     dev.avatar-part-assembler.runtime   （无 Editor 依赖）
  Common/                    稳定错误码、严重级别、数值策略、名称规则、路径词汇
  Components/                AvatarPartInstaller —— 用户添加的纯数据组件
  Profiles/                  ApaPartProfile 及其可序列化组成部分（架构 v4）

Editor/                      dev.avatar-part-assembler.editor    （仅 Editor）
  ApaCore.cs                 唯一的核心门面
  Localization/              本地化层：语言偏好、字符串表、枚举显示名、诊断标签
  Input/ Validation/ Resolution/ Assembly/ Integration/ Authoring/
  NDMF/                      dev.avatar-part-assembler.editor.ndmf
  Preview/                   dev.avatar-part-assembler.editor.preview

Tests/Editor/                dev.avatar-part-assembler.tests.editor（UNITY_INCLUDE_TESTS）
```

依赖方向保持无环：`editor.ndmf` 可以引用 `editor.preview`，而 `editor.preview` 绝不能反向引用
`editor.ndmf`。预览拥有过滤器，NDMF 拥有注册。

**本地化只存在于 Editor 核心程序集**（`Editor/Localization/**`，属于 `dev.avatar-part-assembler.editor`）。
Runtime 程序集保持零依赖、不引入 `UnityEditor`：运行时的预制体必须保持自描述。预览程序集与 NDMF 程序集
本来就引用 Editor 核心程序集，因此它们能使用本地化层而不会产生循环依赖，也不需要新增任何程序集引用。

---

## 术语表

| English | 简体中文 |
| --- | --- |
| Part | 部件 |
| Target renderer / target body renderer | 目标渲染器 / 目标身体渲染器 |
| Part root | 部件根对象 |
| Removal region / removal mask | 移除区域 / 移除遮罩 |
| Texture mask (black/white mask) | 纹理遮罩（黑白遮罩） |
| Mask apply mode | 遮罩应用方式（替换选择 / 添加到选择 / 从选择中减去） |
| Texture readback | 纹理读回（blit 到临时渲染纹理后 `ReadPixels`，唯一路径） |
| Seam (paired loops) | 接缝（成对环） |
| Material semantics | 材质语义 |
| UV semantics | UV 语义 |
| Merge armature | 合并骨架 |
| Armature (target / part) | 骨架（目标骨架 / 部件骨架） |
| Armature-relative bone identity | 骨架相对身份（骨骼相对于自身所选骨架的路径） |
| World-position seam generation | 世界坐标接缝生成 |
| Seam pair / pairing version | 接缝配对 / 配对版本 |
| Weight threshold (weight epsilon) | 权重阈值（权重容差，默认 `1e-5`） |
| Legacy merge names (path / prefix / suffix / inference) | 旧版合并名称（路径 / 前缀 / 后缀 / 名称推断，仅保留序列化往返） |
| Conflict priority | 冲突优先级 |
| Profile | 配置文件 |
| Slot / slot mode | 槽位 / 槽位模式 |
| Blend shape | 形态键 |
| Authoring window | 部件编辑窗口 |
| Installer | 安装器 |
| Preview | 预览 |
| Build | 构建 |
| Acceptance checklist | 验收清单 |

---

## 许可证

MIT，见 [`LICENSE.md`](LICENSE.md)。第三方依赖与所依赖的确切 API 面记录在
[`Third Party Notices.md`](Third Party Notices.md) 与英文 [`Documentation~/OVERVIEW.md`](Documentation~/OVERVIEW.md) 中。
