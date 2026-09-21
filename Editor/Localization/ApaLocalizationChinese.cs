using System;
using System.Collections.Generic;

namespace AvatarPartAssembler.Editor.Localization
{
    /// <summary>
    /// The Simplified Chinese string table: one entry per translatable English string, plus one short description
    /// per allocated <c>APAxxx</c> code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The English text is the key.</b> Keys are byte-identical to the English literal the UI code passes to
    /// <see cref="ApaLocalization.Tr"/>, so a missing entry degrades to English instead of rendering an empty
    /// label or a key-like identifier. A composite entry must carry exactly the same <c>{n}</c> placeholders as its
    /// English key; <c>LocalizationContractTests</c> asserts that, because a mismatch would otherwise surface as a
    /// runtime <see cref="FormatException"/> in a repaint.
    /// </para>
    /// <para>
    /// <b>Terminology.</b> One glossary, used consistently: 部件 (part), 目标渲染器 (target renderer), 部件根对象
    /// (part root), 移除区域 (removal region), 接缝 (seam), 材质语义 (material semantics), UV 语义 (UV semantics),
    /// 合并骨架 (merge armature), 冲突优先级 (conflict priority), 配置文件 (profile).
    /// </para>
    /// <para>
    /// <b>What is deliberately not here.</b> Stable tokens (<c>APAxxx</c>, <c>reason=...</c>, Unity API names,
    /// asset paths, exception text, validation messages) are data rather than UI copy and are rendered unchanged
    /// in both languages, so a Chinese bug report carries the same searchable tokens as an English one.
    /// </para>
    /// </remarks>
    internal static class ApaLocalizationChinese
    {
        /// <summary>The English key to Simplified Chinese entry map.</summary>
        internal static readonly Dictionary<string, string> Strings =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // ---- Window chrome, toolbar, language selector ------------------------------------------
                { "Avatar Part Assembler", "部件装配器" },
                // Menu path segments. These are not translated at runtime: they are the literal spellings the
                // [MenuItem] attributes carry, kept here so the "no menu label without an entry" contract still
                // has a single place to check. The Tools root is never localized.
                { "Part Authoring", "部件编辑" },
                { "Play Mode + Gesture Manager Compatibility", "Play Mode + Gesture Manager 兼容" },
                { "Language", "语言" },
                { "Auto (follow system)", "自动（跟随系统语言）" },
                { "The language of the Avatar Part Assembler user interface. Stored per user, not in the project.",
                  "部件装配器界面语言。按用户保存，不写入项目。" },
                { "undefined", "未定义" },

                // ---- Enum display names ------------------------------------------------------------------
                // Display only: the enum members and their serialized values are never renamed. "Auto" is the
                // material policy; the language selector's Auto option is a different key.
                { "Head", "头部" },
                { "Torso", "躯干" },
                { "Left Arm", "左臂" },
                { "Right Arm", "右臂" },
                { "Left Hand", "左手" },
                { "Right Hand", "右手" },
                { "Left Leg", "左腿" },
                { "Right Leg", "右腿" },
                { "Left Foot", "左脚" },
                { "Right Foot", "右脚" },
                { "Custom", "自定义" },
                { "Replace", "替换" },
                { "Augment", "附加" },
                { "Auto", "自动" },
                { "Use Target", "使用目标材质" },
                { "Keep Part", "保留部件材质" },
                { "Force New", "强制新建槽位" },

                // ---- Toolbar ---------------------------------------------------------------------------
                { "New Draft", "新建草稿" },
                { "Load Profile…", "加载配置文件…" },
                { "Use Unity Selection", "使用 Unity 当前选择" },
                { "Highlights", "高亮显示" },
                { "Started a new draft.", "已开始新草稿。" },
                { "Adopted the Unity selection.", "已采用 Unity 当前选择。" },
                { "Nothing to adopt: select a part with an AvatarPartInstaller, or a target body renderer.",
                  "没有可采用的选中项：请选择一个带 AvatarPartInstaller 的部件，或一个目标身体渲染器。" },

                // ---- Selection section -----------------------------------------------------------------
                { "Selection", "选择" },
                { "Avatar Root", "角色根对象" },
                { "Target Body Renderer", "目标身体渲染器" },
                { "Part Root", "部件根对象" },
                { "Part Renderer", "部件渲染器" },
                // ---- The two Armature selections (M10) -------------------------------------------------
                { "Target Armature", "目标骨架" },
                { "The armature inside the avatar that owns the body's bones. A body bone's identity is its " +
                  "path relative to this object, so it must be the bone level the part's armature corresponds " +
                  "to. It must be the avatar root itself or one of its descendants.",
                  "角色内部拥有身体骨骼的骨架。身体骨骼的身份就是它相对于该对象的路径，因此它必须与部件骨架处于对应的骨骼层级。" +
                  "它必须是角色根对象本身或其子级。" },
                { "Part Armature", "部件骨架" },
                { "The armature inside the part that owns the part's bones. A part bone is the same joint as " +
                  "a body bone exactly when their paths relative to the two selected armatures are identical. " +
                  "It must be the part root itself or one of its descendants.",
                  "部件内部拥有部件骨骼的骨架。当某根部件骨骼与某根身体骨骼相对于各自所选骨架的路径完全一致时，二者才被视为同一骨骼。" +
                  "它必须是部件根对象本身或其子级。" },
                { "Suggest Armatures", "推断骨架" },
                { "Armatures: {0}", "骨架：{0}" },
                { "Armatures: {0}.", "骨架：{0}。" },
                { "Validating", "验证" },
                { "The dry run", "试运行" },
                { "Armature proposal: {0}.", "骨架推断结果：{0}。" },
                { "nothing to propose: both armatures are already selected",
                  "没有可推断的内容：两个骨架都已选择" },
                { "Selected armatures: {0}", "已选骨架：{0}" },
                { "A bone's identity is its path relative to its own Armature, so two Armatures must be the " +
                  "corresponding bone levels of the body and of the part. The legacy merge path, prefix, " +
                  "suffix, and inference controls are gone: the two selections decide the merge.",
                  "骨骼的身份是它相对于自身骨架的路径，因此两个骨架必须是身体与部件相互对应的骨骼层级。" +
                  "旧版的合并路径、前缀、后缀与推断控件已移除：合并完全由这两个选择决定。" },
                { "Select Armatures", "选择骨架" },
                { "Output Folder", "输出文件夹" },
                { "Resolved avatar root", "解析到的角色根对象" },
                { "Find From Part", "从部件查找" },
                { "Capture signature when target changes", "目标变化时捕获签名" },
                { "The selection is not usable yet", "当前选择尚不可用" },

                // ---- Target signature section ----------------------------------------------------------
                { "Target Signature (schema v{0})", "目标签名（架构 v{0}）" },
                { "Renderer Path", "渲染器路径" },
                { "Mesh", "网格" },
                { "Captured", "已捕获" },
                { "Safety data complete", "校验数据完整" },
                { "Vertex Count", "顶点数" },
                { "Submeshes", "子网格" },
                { "Blend Shapes", "形态键" },
                { "Bones", "骨骼" },
                { "Mesh GUID", "网格 GUID" },
                { "(not an asset)", "（不是资产）" },
                { "yes", "是" },
                { "no", "否" },
                { "Missing signature data: {0}. A profile with an incomplete signature is refused by the writer " +
                  "and blocked at build time (APA024).",
                  "签名数据缺失：{0}。签名不完整的配置文件会被写入器拒绝，并在构建时被阻止（APA024）。" },
                { "The selected target mesh changed: its vertex count, submesh layout, or blend shapes no " +
                  "longer match the captured signature. Recapture before saving, or the build will block " +
                  "with APA012 / APA024.",
                  "选中的目标网格已变化：其顶点数、子网格结构或形态键不再与已捕获的签名一致。请在保存前重新捕获，" +
                  "否则构建会以 APA012 / APA024 阻止。" },
                { "The recorded renderer path or mesh asset GUID no longer matches the live target. This is " +
                  "advisory: the safety fields still match, so the profile remains usable and the build " +
                  "will not block because of it. Recapture to store the current path and GUID.",
                  "记录的渲染器路径或网格资产 GUID 已与当前目标不一致。这仅为提示：校验数据仍然一致，" +
                  "配置文件依然可用，构建不会因此被阻止。重新捕获即可记录当前的路径与 GUID。" },
                { "Capture Signature", "捕获签名" },
                { "Clear Signature", "清除签名" },
                { "Cleared the captured signature.", "已清除已捕获的签名。" },

                // ---- Part identity section -------------------------------------------------------------
                { "Part Identity", "部件标识" },
                { "Display Name", "显示名称" },
                { "Slot", "槽位" },
                { "Slot Mode", "槽位模式" },
                { "Replace: this part owns the slot exclusively (two Replace parts on one non-Custom slot block " +
                  "with APA013). Augment: this part shares the slot and must declare no removal triangles.",
                  "替换：本部件独占该槽位（同一非自定义槽位上的两个“替换”部件会以 APA013 阻止）。" +
                  "附加：本部件共享该槽位，且不得声明移除三角形。" },
                { "Conflict Priority", "冲突优先级" },
                { "0 means undeclared. A removal-region overlap is resolved only when every claimant declares a " +
                  "priority greater than zero and the highest is unique (APA035); otherwise it blocks with " +
                  "APA010. Negative values block with APA037.",
                  "0 表示未声明。只有当每个声明方都给出大于 0 的优先级且最高值唯一时，移除区域重叠才会被解析" +
                  "（APA035）；否则以 APA010 阻止。负值以 APA037 阻止。" },
                { "Stable Part Id", "稳定部件 ID" },
                { "Assign", "分配" },
                { "Part Id", "部件 ID" },
                { "Repair Part Id", "修复部件 ID" },
                { "This profile carries no stored part id, so a stable one is derived from the profile asset's " +
                  "GUID: {0}. The part installs normally and the identity does not change between preview, " +
                  "validation, sorting, and the build. Press Repair Part Id to store it on the asset.",
                  "该配置文件没有保存部件 ID，因此会从配置文件资产的 GUID 派生一个稳定 ID：{0}。" +
                  "部件可正常安装，且该标识在预览、验证、排序和构建之间保持不变。点击“修复部件 ID”可将其写入资产。" },
                { "This profile carries no part id and is not a project asset, so no stable identity can be " +
                  "derived for it. Save it as an asset inside the project, then repair it.",
                  "该配置文件没有部件 ID，且不是项目资产，因此无法为它派生稳定标识。请先将其保存为项目内的资产，再进行修复。" },
                { "The part id could not be repaired ({0}). Nothing was written.",
                  "无法修复部件 ID（{0}）。未写入任何内容。" },
                { "Stored part id '{0}' on the profile.", "已将部件 ID '{0}' 写入配置文件。" },
                { "The stable id is the ordering key. It survives renaming and hierarchy moves, so it is never " +
                  "derived from the name.",
                  "稳定 ID 是排序键。它在重命名和层级移动后保持不变，因此绝不从名称派生。" },
                { "An Augment part declares removal triangles. An augmenting part does not own a body region, so " +
                  "the profile will be refused with APA013 (reason=augment-declares-removal). Clear the removal " +
                  "region or switch the slot mode back to Replace.",
                  "该“附加”部件声明了移除三角形。附加部件不拥有身体区域，因此配置文件会被以 APA013 " +
                  "（reason=augment-declares-removal）拒绝。请清空移除区域，或将槽位模式改回“替换”。" },

                // ---- Output section --------------------------------------------------------------------
                { "Output", "输出" },
                { "Profile Asset", "配置文件资产" },
                { "Part Prefab", "部件预制体" },
                { "Profile asset path", "配置文件资产路径" },
                { "Part prefab path", "部件预制体路径" },
                { "Choose where the profile is written", "选择配置文件的写入位置" },
                { "Choose where the prefab is written", "选择预制体的写入位置" },
                { "Default Paths", "默认路径" },
                { "Allow replacing an existing asset (asked again when unchecked)",
                  "允许替换已存在的资产（未勾选时会再次询问）" },
                { "Existing profile", "已存在的配置文件" },
                { "Load Existing Profile", "加载已有配置文件" },
                { "Read a saved profile asset into the draft. The asset itself is never written until you save " +
                  "it, and loading replaces the whole draft, so the fields below show the loaded profile.",
                  "把已保存的配置文件资产读入草稿。在你保存之前，该资产本身不会被写入；加载会替换整个草稿，" +
                  "因此下方的各个字段显示的是加载进来的配置文件。" },
                { "Profile path problem: {0}", "配置文件路径问题：{0}" },
                { "exists — the draft differs from it", "已存在 — 草稿与其不一致" },
                { "exists — the draft matches it", "已存在 — 草稿与其一致" },

                // ---- Protected part mesh (output option) ------------------------------------------------
                { "Protect the part mesh (write an encrypted payload instead of the mesh)",
                  "保护部件网格（写入加密载荷而不是网格）" },
                { "Off: the prefab references the source mesh exactly as it always has.",
                  "关闭：预制体像以往一样直接引用源网格。" },
                { "Protected Mesh Asset", "受保护网格资产" },
                { "A protected mesh asset already exists at this path. Replacing it is asked for again when the " +
                  "prefab replacement is confirmed.",
                  "该路径已存在受保护网格资产。确认替换预制体时会再次询问是否替换它。" },
                { "The part mesh is written to the protected asset as an authenticated, encrypted payload and " +
                  "the prefab's part renderer is saved with no mesh, so the prefab no longer depends on the " +
                  "source mesh or its model file. The payload must be delivered together with the prefab: a " +
                  "prefab whose payload is missing cannot be assembled. This protects the distribution format " +
                  "and detects tampering; it is not unextractable DRM, because a build that runs in the Editor " +
                  "can be observed while it runs. Materials, textures, bones, and animation remain ordinary " +
                  "assets. Recreate the protected prefab after the source mesh changes.",
                  "部件网格会以经过认证的加密载荷写入受保护资产，预制体的部件渲染器则保存为不带网格，" +
                  "因此预制体不再依赖源网格或其模型文件。该载荷必须与预制体一起分发：缺少载荷的预制体无法装配。" +
                  "这保护的是分发格式并能检测篡改，并不是不可提取的 DRM —— 在编辑器中运行的构建过程可以被观察。" +
                  "材质、贴图、骨骼与动画仍然是普通资产。源网格变化后必须重新创建受保护预制体。" },
                { "Wrote the protected mesh payload to '{0}'.", "已将受保护网格载荷写入 '{0}'。" },
                { "Cleared the part renderer's mesh reference in the saved prefab ('{0}'); the geometry now lives " +
                  "only in the protected payload.",
                  "已清除保存的预制体中部件渲染器的网格引用（'{0}'）；几何体现在只存在于受保护载荷中。" },
                { "The existing prefab at '{0}' was replaced with the scene part, but the protected payload could " +
                  "not be published because {1}. The prefab is present but unconfigured; restore it from version " +
                  "control if that content mattered.",
                  "位于 '{0}' 的已有预制体已被场景中的部件替换，但由于{1}，受保护载荷未能发布。该预制体存在但未配置完成；" +
                  "如果原有内容重要，请从版本控制恢复。" },
                { "The existing prefab at '{0}' was replaced with the scene part, but the written prefab still " +
                  "depends on the source mesh or its model file, so it was removed again. Restore the prefab from " +
                  "version control if its previous content mattered.",
                  "位于 '{0}' 的已有预制体已被场景中的部件替换，但写出的预制体仍然依赖源网格或其模型文件，因此已被再次移除。" +
                  "如果原有内容重要，请从版本控制恢复该预制体。" },
                { "The existing prefab at '{0}' was replaced with the scene part, but the written prefab does not " +
                  "carry the protected mesh reference, so it was removed again. Restore the prefab from version " +
                  "control if its previous content mattered.",
                  "位于 '{0}' 的已有预制体已被场景中的部件替换，但写出的预制体没有携带受保护网格引用，因此已被再次移除。" +
                  "如果原有内容重要，请从版本控制恢复该预制体。" },
                { "the part still references the source mesh or model file '{0}' from {1} other component " +
                  "reference(s)",
                  "部件仍通过另外 {1} 个组件引用指向源网格或模型文件 '{0}'" },
                { "the reference scan stopped at its property budget after {0} properties, so the prefab cannot be " +
                  "proven free of the source mesh",
                  "引用扫描在检查 {0} 个属性后达到预算上限，因此无法证明该预制体不含源网格" },

                // ---- Removal section -------------------------------------------------------------------
                { "Removal Region", "移除区域" },
                { "Selected", "已选" },
                { "Clear", "清空" },
                { "Removal set problems", "移除集合问题" },
                { "Highlights are off in the toolbar, so the Scene View overlays are hidden. Turning them on " +
                  "draws the removal overlay.",
                  "工具栏中的“高亮显示”已关闭，因此场景视图叠加层被隐藏。开启后会绘制移除叠加层。" },

                // ---- Texture mask (black/white mask selection) -----------------------------------------
                { "Texture Mask", "纹理遮罩（黑白遮罩）" },
                { "Mask Texture", "遮罩纹理" },
                { "A black/white texture sampled through the target mesh's UVs. White selects removal triangles " +
                  "and black keeps them; Invert reverses that. The texture is only an authoring input: the " +
                  "profile stores the resulting triangle set, never the texture.",
                  "通过目标网格 UV 采样的黑白纹理。白色会选中要移除的三角形，黑色则保留；勾选“反相”可反转这一含义。纹理只是编辑阶段的输入：配置文件中保存的是生成的三角形集合，而不是纹理本身。" },
                { "UV Channel", "UV 通道" },
                { "The target mesh UV channel the mask addresses. A mask painted against UV1 does nothing " +
                  "when UV0 is read, so the channel is part of the input rather than a preference.",
                  "遮罩所对应的目标网格 UV 通道。画在 UV1 上的遮罩在读取 UV0 时不会产生任何效果，因此通道属于输入的一部分，而不是偏好设置。" },
                { "Threshold", "阈值" },
                { "Brightness at or above which a sample counts as selected. A sample must reach the " +
                  "threshold to pass; with Invert on, its brightness is replaced by 1 minus the brightness " +
                  "first.",
                  "采样点亮度达到或超过该值即视为选中。采样点必须达到阈值才算通过；勾选“反相”时，会先用 1 减去亮度再比较。" },
                { "Invert", "反相" },
                { "Treat black as the removal color and white as the keep color.",
                  "把黑色视为移除色，把白色视为保留色。" },
                { "Apply Mode", "应用方式" },
                { "How the generated triangles are combined with the current removal set. Replace is the " +
                  "default; Add and Subtract adjust the existing selection, which is how several masks build one " +
                  "region.",
                  "生成的三角形如何与当前移除集合合并。默认是“替换选择”；“添加到选择”和“从选择中减去”用于在已有选择上增减，这也是用多张遮罩拼出同一区域的方式。" },
                // The apply-mode popup's own option labels. The enum members (ReplaceSelection and friends) are
                // never shown in the UI.
                { "Replace Selection", "替换选择" },
                { "Add To Selection", "添加到选择" },
                { "Subtract From Selection", "从选择中减去" },
                { "Apply Mask", "应用遮罩" },
                { "Sample rule: 7 points per triangle (3 vertices, 3 edge midpoints, centroid); the triangle is " +
                  "selected when at least 4 samples are at or above the threshold; brightness is RGB luminance " +
                  "and alpha is ignored.",
                  "采样规则：每个三角形取 7 个采样点（3 个顶点、3 条边中点、1 个重心）；当至少 4 个采样点达到或超过阈值时选中该三角形；亮度按 RGB 亮度计算，忽略 alpha。" },
                { "Select a target body renderer first: the mask is read through its mesh UVs.",
                  "请先选择目标身体渲染器：遮罩需要通过其网格 UV 读取。" },
                { "Assign a mask texture to generate a selection from.",
                  "请指定一张遮罩纹理以生成选择。" },
                { "Mesh '{0}' is not readable, so its UVs cannot be read. Enable Read/Write in its import " +
                  "settings.",
                  "网格“{0}”不可读，因此无法读取其 UV。请在其导入设置中启用 Read/Write。" },
                { "Mesh '{0}' carries no UV channel {1}. Choose a channel the mesh has, or paint the mask " +
                  "against one it does.",
                  "网格“{0}”没有 UV 通道 {1}。请选择该网格实际拥有的通道，或按它拥有的通道重新绘制遮罩。" },
                { "Apply Texture Mask", "应用纹理遮罩" },
                { "Mask apply failed: {0}", "遮罩应用失败：{0}" },
                { "Mask apply failed: the mask could not be read.", "遮罩应用失败：无法读取遮罩。" },
                { "Texture mask: no selection was generated. The refusal is shown in the Texture " +
                  "Mask block.",
                  "纹理遮罩：未生成任何选择。拒绝原因显示在“纹理遮罩”区块中。" },
                { "Texture mask: {0} of {1} triangle(s) matched.",
                  "纹理遮罩：匹配 {1} 个三角形中的 {0} 个。" },
                { "The mask generated no triangles, so the selection is unchanged at {0}.",
                  "遮罩未生成任何三角形，选择保持不变，仍为 {0} 个。" },
                { "Added {0} triangle(s) to the selection: {1} before, {2} after ({3} were already " +
                  "selected).",
                  "已向选择中加入 {0} 个三角形：之前 {1} 个，之后 {2} 个（其中 {3} 个原本已被选中）。" },
                { "None of the {0} generated triangle(s) were in the selection, so it is unchanged at {1}.",
                  "生成的 {0} 个三角形都不在选择中，选择保持不变，仍为 {1} 个。" },
                { "Removed {0} triangle(s) from the selection: {1} before, {2} after.",
                  "已从选择中移除 {0} 个三角形：之前 {1} 个，之后 {2} 个。" },
                { "The mask matched no triangle, so the selection was replaced with an empty set " +
                  "({0} address(es) cleared).",
                  "遮罩未匹配任何三角形，选择已被替换为空集合（清除了 {0} 个地址）。" },
                { "Replaced the selection with {0} generated triangle(s), {1} address(es) before.",
                  "已用生成的 {0} 个三角形替换选择，替换前有 {1} 个地址。" },

                // ---- Seam section (world-position pairing, M10) ----------------------------------------
                { "Seam (paired loops)", "接缝（成对环）" },
                { "Pairs", "配对" },
                { "Seam pairing: {0}", "接缝配对：{0}" },
                { "no seam pairs", "没有接缝配对" },
                { "unpaired legacy seam", "未配对的旧版接缝" },
                { "unpaired legacy seam (APA042)", "未配对的旧版接缝（APA042）" },
                { "explicit, written by this window", "已显式配对，由本窗口写入" },
                { "legacy, unpaired (APA042)", "旧版、未配对（APA042）" },
                { "no seam (the part does not weld)", "没有接缝（该部件不焊接）" },
                { "{0} seam pair(s)", "{0} 对接缝" },
                { "{0} seam pair(s); first: {1} -> {2}", "{0} 对接缝；前几对：{1} -> {2}" },
                { "Tolerance (world units)", "容差（世界单位）" },
                { "How close two vertices must be in world space to be paired. The comparison uses the " +
                  "rest pose, not the current animated pose, and the value is in world units because that " +
                  "is the quantity a seam is authored in.",
                  "两个顶点在世界空间中距离多近才被视为一对。比较基于静置姿势（而非当前动画姿势）；" +
                  "数值以世界单位表示，因为接缝正是按世界坐标制作的。" },
                { "Generate From World Positions", "按世界坐标生成接缝" },
                { "Generate Seam From World Positions", "按世界坐标生成接缝" },
                { "Clear Seam", "清空接缝" },
                { "Cleared the seam. This part now declares no seam.", "已清空接缝。该部件现在不声明任何接缝。" },
                { "Both meshes are read in their rest pose (the shared mesh, never a baked pose) and every " +
                  "world-coincident pair within the tolerance is written as one weld. Only part vertices that " +
                  "carry the selected candidate color may pair; the target body is matched spatially and needs " +
                  "no vertex colors. Leave the seam empty when this part does not weld to the body.",
                  "两个网格都按静置姿势读取（使用共享网格，绝不使用烘焙姿势），容差内所有世界坐标重合的顶点都会写成一对焊接。" +
                  "只有带有选中候选颜色的部件顶点才允许配对；目标身体按空间位置匹配，不需要顶点色。" +
                  "如果该部件不与身体焊接，请保持接缝为空。" },
                { "This seam was authored before explicit pairing: its two lists are unordered sets and the " +
                  "build refuses them (APA042). Generate it from world positions to write the pairing.",
                  "该接缝是在显式配对之前制作的：它的两个列表是无序集合，构建会拒绝它（APA042）。" +
                  "请按世界坐标重新生成，以写入配对关系。" },
                { "Seam generation failed: {0}", "接缝生成失败：{0}" },
                { "Seam generation produced no pairs. The refusal is shown in the Seam block.",
                  "接缝生成没有产生任何配对。拒绝原因显示在“接缝”区块中。" },
                { "Seam: {0}", "接缝：{0}" },
                { "seam generation failed", "接缝生成失败" },
                { "{0} seam pair(s) within a world tolerance of {1}; {2} of {3} part vertex(es) had a free " +
                  "counterpart.",
                  "在世界容差 {1} 内生成 {0} 对接缝；{3} 个部件顶点中有 {2} 个找到了未占用的对应顶点。" },
                { "Seam problems", "接缝问题" },
                { "not resolved", "未解析" },
                { "no seam candidate color", "没有接缝候选颜色" },
                { "{0} seam candidate vertex(es) carrying the color {1}",
                  "{1} 颜色的 {0} 个接缝候选顶点" },
                { "Candidate Color Code", "候选颜色代码" },
                { "The vertex color that marks a seam candidate on the part mesh, written as #RRGGBB. " +
                  "#RRGGBBAA is accepted as well, and is shown when the alpha is not opaque. A part vertex may " +
                  "pair only when all four channels of its stored Mesh.colors32 entry equal this color exactly " +
                  "— there is no tolerance, and the alpha channel participates. The target body mesh is not " +
                  "filtered by color: its vertices are matched by world position. Paint the part's seam ring " +
                  "with this color in the modelling tool, or write Mesh.colors32 before generating the seam.",
                  "标记部件网格上接缝候选顶点的顶点颜色，写作 #RRGGBB。也接受 #RRGGBBAA；当 alpha 不是不透明时会以该形式显示。" +
                  "只有当部件顶点保存的 Mesh.colors32 四个通道与该颜色完全相等时，它才是候选顶点——没有容差，且 alpha 通道同样参与比较。" +
                  "目标身体网格不按颜色过滤：它的顶点按世界坐标匹配。" +
                  "请在建模工具中用该颜色绘制部件的接缝环，或在生成接缝前写入 Mesh.colors32。" },
                { "Color code '{0}' is not a color. Write #RRGGBB, or #RRGGBBAA to include the alpha channel; " +
                  "the previous color {1} is still in effect.",
                  "颜色代码 '{0}' 不是有效的颜色。请写作 #RRGGBB，或使用 #RRGGBBAA 以包含 alpha 通道；" +
                  "之前的颜色 {1} 仍然有效。" },
                { "Matched color: {0} (exact Color32 equality, alpha included)",
                  "匹配颜色：{0}（Color32 完全相等，含 alpha）" },
                { "Target seam candidates: {0}", "目标接缝候选：{0}" },
                { "Part seam candidates: {0}", "部件接缝候选：{0}" },
                { "{0} spatial target vertex(es) (paired by world position, not by color)",
                  "按世界坐标（而非颜色）配对的 {0} 个目标顶点" },
                { "Merge Check Overlay", "合并检查叠加层" },
                { "Draw the prospective pairing of the selected candidate colors in the Scene View instead of " +
                  "the ordinary overlays: candidates the matcher would pair are green, candidates with no " +
                  "counterpart within the tolerance are red. The removal, candidate, and stored-seam overlays " +
                  "are hidden while this is on and reappear unchanged when it is switched off. Nothing is " +
                  "written: the stored seam and the profile are untouched.",
                  "在场景视图中用“预演配对”取代常规叠加层：匹配器会配对的顶点为绿色，容差内没有对应顶点的部件候选顶点为红色。" +
                  "开启时移除、候选与已存接缝叠加层会被隐藏，关闭后原样恢复。该模式不写入任何数据：已存接缝与配置文件保持不变。" },
                { "merge check unavailable", "无法执行合并检查" },
                { "{0} matched pair(s); {1} of {2} target and {3} of {4} part candidate(s) unmatched at a world " +
                  "tolerance of {5}",
                  "已配对 {0} 对；世界容差 {5} 下，{2} 个目标候选中有 {1} 个、{4} 个部件候选中有 {3} 个没有对应顶点" },
                { "{0} matched pair(s); the target side is spatial ({1} target vertex(es)); {2} of {3} part " +
                  "candidate(s) unmatched at a world tolerance of {4}",
                  "已配对 {0} 对；目标侧为空间匹配（{1} 个目标顶点）；世界容差 {4} 下，{3} 个部件候选中有 {2} 个没有对应顶点" },
                { "select a target renderer and a part renderer with meshes to run the merge check",
                  "请选择带网格的目标渲染器与部件渲染器，才能执行合并检查" },
                { "Avatar Part Assembler — Merge Check\nmerge check: {0}\ntolerance (world units): {1}\n" +
                  "green: would pair   red: part candidate with no counterpart",
                  "Avatar Part Assembler — 合并检查\n合并检查：{0}\n容差（世界单位）：{1}\n" +
                  "绿色：会配对   红色：没有对应顶点的部件候选" },
                { "Removal Overlay", "移除叠加层" },
                { "Candidate Overlay", "候选叠加层" },
                { "Show the red triangles selected by the removal mask in the Scene View.",
                  "在场景视图中显示移除遮罩选中的红色三角形。" },
                { "Show the green seam-candidate vertices and the stored seam points in the Scene View.",
                  "在场景视图中显示绿色接缝候选顶点与已保存的接缝点。" },
                { "Show matched seam candidates in green and unmatched part candidates in red.",
                  "将已匹配的接缝候选显示为绿色，将未匹配的部件候选显示为红色。" },
                { "Draw the triangles the removal mask generated in the Scene View. Off by default. It is a " +
                  "read-only visualization of the authored removal set: it follows the target's current " +
                  "skinned pose, while the removal set itself stays a set of triangle addresses, and " +
                  "clicking in the Scene View never edits it.",
                  "在场景视图中绘制移除遮罩生成的三角形。默认关闭。它只是对已制作移除集合的只读可视化：" +
                  "会跟随目标当前的蒙皮姿势，而移除集合本身始终是一组三角形地址，在场景视图中点击不会修改它。" },
                { "preview: current skinned pose; seam data stays rest-pose",
                  "预览：当前蒙皮姿势；接缝数据仍为静置姿势" },
                { "\npreview: current skinned pose; seam data stays rest-pose",
                  "\n预览：当前蒙皮姿势；接缝数据仍为静置姿势" },
                { "\npreview: current skinned pose; matched/unmatched indices stay rest-pose",
                  "\n预览：当前蒙皮姿势；匹配/未匹配的索引仍为静置姿势" },
                { "(missing)", "（缺失）" },
                { "Mesh Fingerprint", "网格指纹" },
                { "mesh fingerprint", "网格指纹" },
                { "Capture Part Mesh Fingerprint", "捕获部件网格指纹" },
                { "… and {0} more (re-apply the mask to change the whole set)",
                  "… 另有 {0} 项（重新应用遮罩可修改整个集合）" },

                // ---- Removal address list (collapsed summary, M10) -------------------------------------
                { "Addresses ({0})", "地址（{0}）" },
                { "Collapse", "折叠" },
                { "Showing the summary only: {0} address(es) are selected.",
                  "仅显示摘要：已选中 {0} 个地址。" },

                // ---- UV section ------------------------------------------------------------------------
                { "UV Semantics", "UV 语义" },
                { "Infer From Part Mesh", "从部件网格推断" },
                { "Add Row", "添加行" },
                { "Source channel of the part mesh that carries each semantic. Names are trimmed and compared " +
                  "ordinally (case-sensitive).",
                  "部件网格中承载各语义的来源通道。名称会去除首尾空白并按序数比较（区分大小写）。" },
                { "UV semantic problems", "UV 语义问题" },
                { "Select a readable part mesh before inferring UV semantics.",
                  "请先选择一个可读的部件网格，再推断 UV 语义。" },
                { "Inferred {0} UV semantic(s) from the part mesh.", "已从部件网格推断出 {0} 条 UV 语义。" },

                // ---- Material section ------------------------------------------------------------------
                { "Material Semantics", "材质语义" },
                { "Infer From Materials", "从材质推断" },
                { "The material field is this profile's authoring default. The build takes the part renderer's " +
                  "own material in that slot and falls back to this asset only when the renderer has none " +
                  "there, so replacing a material on the renderer is what changes the built avatar and is " +
                  "never written back into this profile. A material must be a project asset: a scene material " +
                  "cannot be stored in a reusable profile (APA034). The asset name is never used as the " +
                  "semantic.",
                  "材质字段是本配置文件的创作默认值。构建会采用部件渲染器该槽位上的材质，仅当渲染器该槽位没有材质时才回退到此资产；" +
                  "因此在渲染器上替换材质才会改变构建出的角色，且绝不会写回本配置文件。材质必须是项目资产：场景中的材质无法存入可复用的配置文件（APA034）。" +
                  "资产名称绝不会被用作语义。" },
                { "Material semantic problems", "材质语义问题" },
                { "Select a part renderer before inferring material semantics.",
                  "请先选择部件渲染器，再推断材质语义。" },
                { "Inferred {0} material semantic(s).", "已推断出 {0} 条材质语义。" },

                // ---- Bones and blend shapes ------------------------------------------------------------
                { "Bones And Blend Shapes", "骨骼与形态键" },
                { "Merge Armature", "合并骨架" },
                { "When enabled, a transient Modular Avatar merge configuration is generated at build time on " +
                  "the selected part armature, targeting the selected target armature. It is written with no " +
                  "prefix, no suffix, and no inference, so it matches bones by exact name — which is the same " +
                  "relation the armature-relative identity describes.",
                  "启用后，构建时会在所选部件骨架上生成一个临时的 Modular Avatar 合并配置，并指向所选目标骨架。" +
                  "该配置不写入前缀、后缀，也不做推断，因此按骨骼名称精确匹配——这正是“骨架相对身份”所描述的关系。" },
                { " (armature merge is off, so no merge is generated)",
                  "（骨架合并已关闭，因此不会生成合并配置）" },
                { "The legacy merge target path, prefix, suffix, and inference fields are no longer shown: the " +
                  "two Armature selections decide the merge, and nothing is inferred from names.",
                  "旧版的合并目标路径、前缀、后缀与推断字段不再显示：合并完全由两个骨架选择决定，且不会从名称推断任何内容。" },
                { "Allow Part-Only Blend Shapes", "允许仅部件形态键" },
                { "The strict blend shape rules (frame agreement, zero seam deltas for part-only shapes) are " +
                  "always enforced and are not configurable.",
                  "严格的形态键规则（帧一致性、仅部件形态键在接缝处增量为零）始终强制生效，不可配置。" },

                // ---- Actions ---------------------------------------------------------------------------
                { "Actions", "操作" },
                { "Validate", "验证" },
                { "Dry-Run Assembly", "试运行装配" },
                { "Save Profile Asset", "保存配置文件资产" },
                { "Select Profile", "选中配置文件" },
                { "Create Part Prefab", "创建部件预制体" },
                { "Update Installer On Prefab", "更新预制体上的安装器" },
                { "The prefab references the saved profile and resolves the target body through the captured " +
                  "renderer path. The authoring scene's target renderer is never serialized into the prefab.",
                  "预制体引用已保存的配置文件，并通过捕获的渲染器路径解析目标身体。" +
                  "制作场景中的目标渲染器绝不会被序列化进预制体。" },
                { "Last write reported", "最近一次写入报告" },
                { "Plan succeeded: {0}. No mesh was created.", "规划成功：{0}。未创建任何网格。" },
                { "Plan failed. Nothing would be built.", "规划失败。不会构建任何内容。" },

                // ---- Diagnostics -----------------------------------------------------------------------
                { "Diagnostics", "诊断" },
                { "Not validated yet.", "尚未验证。" },
                { "Copy", "复制" },
                { "Copied {0} diagnostic(s) to the clipboard.", "已复制 {0} 条诊断到剪贴板。" },
                { "not validated", "尚未验证" },
                { "{0} error(s), {1} warning(s), {2} info", "{0} 个错误、{1} 个警告、{2} 条信息" },

                // ---- Scene View tool -------------------------------------------------------------------
                { "Avatar Part Assembler\nremoval: {0}\nseam: {1}",
                  "部件装配器\n移除区域：{0}\n接缝：{1}" },
                { "\noverlays are off: turn on Highlights in the Part Authoring toolbar",
                  "\n叠加层已关闭：请在“部件编辑”工具栏中开启“高亮显示”" },
                { "\nshowing the first {0} removed triangles", "\n仅显示前 {0} 个移除三角形" },

                // ---- Status lines composed by the window -----------------------------------------------
                { "Validation: {0}", "验证：{0}" },
                { "Dry run: {0} — {1}", "试运行：{0} — {1}" },
                { " (no live target was captured, so only authoring data was checked)",
                  "（未捕获实时目标，因此只检查了制作数据）" },
                { "Loaded profile '{0}'. The asset is only read until you save.",
                  "已加载配置文件 '{0}'。在你保存之前，该资产只会被读取。" },
                { "Loaded profile '{0}'. The asset is only read until you save. It carries no stored part id, " +
                  "so the draft holds the stable id derived from the profile asset's GUID; saving stores it.",
                  "已加载配置文件 '{0}'。在你保存之前，该资产只会被读取。它没有保存部件 ID，因此草稿中保存的是" +
                  "从配置文件资产 GUID 派生的稳定 ID；保存后即写入资产。" },
                { "No profile asset found at '{0}'. Profiles must live inside the project's Assets folder.",
                  "在 '{0}' 找不到配置文件资产。配置文件必须位于项目的 Assets 文件夹内。" },
                { "Open an Avatar Part profile", "打开部件配置文件" },
                { "Profile asset", "配置文件资产" },
                { "Saving the profile", "保存配置文件" },
                { "Creating the prefab", "创建预制体" },
                { "Updating the prefab", "更新预制体" },
                { "{0} needs a complete selection: avatar root, target body renderer, part root, and part " +
                  "renderer. A profile that cannot be validated against a body must not be written.",
                  "{0}需要完整的选择：角色根对象、目标身体渲染器、部件根对象和部件渲染器。" +
                  "无法针对身体完成验证的配置文件不得写入。" },
                { "{0} blocked: {1}. Fix the diagnostics below; nothing was written.",
                  "{0}已被阻止：{1}。请修复下面的诊断；未写入任何内容。" },
                { "{0} blocked: the assembly cannot be planned. {1}. Nothing was written.",
                  "{0}已被阻止：无法规划装配。{1}。未写入任何内容。" },
                { "Captured the target signature: {0}.", "已捕获目标签名：{0}。" },
                { "Captured the target signature with {0} issue(s); see the diagnostics below.",
                  "已捕获目标签名，但有 {0} 个问题；请查看下方诊断。" },
                { "{0} blocked: the target signature could not be captured, and a profile without one cannot be " +
                  "verified. The refusal is shown in the 'Last write reported' block.",
                  "{0}已被阻止：无法捕获目标签名，而没有签名的配置文件无法验证。拒绝原因显示在“最近一次写入报告”区块中。" },

                // ---- Confirm and overwrite dialogs -----------------------------------------------------
                { "Replace existing profile?", "替换已有配置文件？" },
                { "A profile already exists at\n\n{0}\n\nReplacing it keeps the asset's GUID, so prefabs that " +
                  "reference it keep working. Replace its contents?",
                  "以下位置已存在配置文件：\n\n{0}\n\n替换会保留该资产的 GUID，因此引用它的预制体仍然可用。" +
                  "要替换其内容吗？" },
                { "Replace existing prefab?", "替换已有预制体？" },
                { "A prefab already exists at\n\n{0}\n\nReplacing it writes the current scene part over the " +
                  "existing prefab content. Use 'Update installer on prefab' instead to keep the prefab's own " +
                  "content. Replace it?",
                  "以下位置已存在预制体：\n\n{0}\n\n替换会用当前场景中的部件覆盖已有预制体的内容。" +
                  "若要保留预制体自身的内容，请改用“更新预制体上的安装器”。要替换吗？" },
                { "Cancel", "取消" },
                { "Replace existing protected mesh asset?", "替换已有的受保护网格资产？" },
                { "Replace existing prefab and protected mesh asset?", "替换已有的预制体和受保护网格资产？" },
                { "A protected mesh asset already exists at\n\n{0}\n\nReplacing it keeps the asset's GUID, so a " +
                  "prefab that already references it keeps working, and it is restored if the prefab cannot be " +
                  "written. Replace it?",
                  "以下位置已存在受保护网格资产：\n\n{0}\n\n替换会保留该资产的 GUID，因此已经引用它的预制体仍然可用；" +
                  "若预制体写入失败，它会被还原。要替换吗？" },
                { "A prefab already exists at\n\n{0}\n\nThe protected mesh asset at\n\n{1}\n\nis replaced as well, " +
                  "keeping its GUID so a prefab that already references it keeps working. Use 'Update installer " +
                  "on prefab' instead to keep the prefab's own content. Replace both?",
                  "以下位置已存在预制体：\n\n{0}\n\n位于\n\n{1}\n\n的受保护网格资产也会一并替换，并保留其 GUID，" +
                  "因此已经引用它的预制体仍然可用。若要保留预制体自身的内容，请改用“更新预制体上的安装器”。" +
                  "要同时替换两者吗？" },
                { "Profile write cancelled. Nothing was written to '{0}'.",
                  "已取消写入配置文件。未向 '{0}' 写入任何内容。" },
                { "Prefab creation cancelled. Nothing was written to '{0}'.",
                  "已取消创建预制体。未向 '{0}' 写入任何内容。" },
                { "Save the profile first: no profile asset exists at '{0}'.",
                  "请先保存配置文件：'{0}' 处不存在配置文件资产。" },
                { "Save the profile first: the draft differs from '{0}', and the prefab must reference what was " +
                  "validated.",
                  "请先保存配置文件：草稿与 '{0}' 不一致，而预制体必须引用经过验证的内容。" },

                // ---- Undo labels -----------------------------------------------------------------------
                { "Edit Avatar Part", "编辑部件" },
                { "New Part Draft", "新建部件草稿" },
                { "Adopt Unity Selection", "采用 Unity 选择" },
                { "Find Avatar Root", "查找角色根对象" },
                { "Capture Compatibility Signature", "捕获兼容性签名" },
                { "Assign Part Id", "分配部件 ID" },
                { "Change Output Paths", "更改输出路径" },
                { "Reset Output Paths", "重置输出路径" },
                { "Clear Removal Mask", "清空移除遮罩" },
                { "Infer UV Semantics", "推断 UV 语义" },
                { "Add UV Semantic", "添加 UV 语义" },
                { "Edit UV Semantic", "编辑 UV 语义" },
                { "Move UV Semantic", "移动 UV 语义" },
                { "Remove UV Semantic", "移除 UV 语义" },
                { "Infer Material Semantics", "推断材质语义" },
                { "Add Material Semantic", "添加材质语义" },
                { "Edit Material Semantic", "编辑材质语义" },
                { "Move Material Semantic", "移动材质语义" },
                { "Remove Material Semantic", "移除材质语义" },
                { "Load Avatar Part Profile", "加载部件配置文件" },
                { "Assign Avatar Part Profile", "指定部件配置文件" },
                { "Create Avatar Part Profile", "创建部件配置文件" },
                { "Update Avatar Part Profile", "更新部件配置文件" },

                // ---- Installer inspector ---------------------------------------------------------------
                { "{0} installers are selected. Status and shortcuts apply to '{1}'.",
                  "已选中 {0} 个安装器。状态与快捷操作作用于 '{1}'。" },
                { "The inspector could not find the installer's serialized fields. This is an internal " +
                  "consistency error in the package.",
                  "检查器找不到安装器的序列化字段。这是本包内部的 consistency 错误。" },
                { "Profile", "配置文件" },
                { "The authored part profile.", "制作好的部件配置文件。" },
                { "Root of the part geometry. Empty means this GameObject.", "部件几何体的根对象。留空表示当前 GameObject。" },
                { "Target Renderer Object", "目标渲染器对象" },
                { "Optional explicit target body. Empty resolves the target from the profile's captured renderer path.",
                  "可选的目标身体。留空时通过配置文件捕获的渲染器路径解析目标。" },
                { "Enabled For Build", "参与构建" },
                { "Status", "状态" },
                { "No profile is assigned, so this part cannot be installed. Create a profile or assign an " +
                  "existing one.",
                  "未指定配置文件，因此无法安装该部件。请创建配置文件或指定已有配置文件。" },
                // ---- Installer health indicator (concise end-user verdict) ------------------------------
                { "This part is ready: validation reported {0}.", "该部件已就绪：验证结果为 {0}。" },
                { "This part has a problem: validation reported {0}. See the details below.",
                  "该部件存在问题：验证结果为 {0}。请查看下方的详细信息。" },
                { "This part has not been validated yet.", "尚未验证该部件。" },
                { "Validation results were cleared. Press Validate to check this part again.",
                  "验证结果已清除。再次点击“验证”即可重新检查该部件。" },
                { "This installer belongs to a prefab asset, so it can only be validated after it is placed " +
                  "under an avatar in a scene.",
                  "该安装器属于预制体资产，只有把它放到场景中的角色下之后才能进行验证。" },
                { "Part", "部件" },
                { "(unnamed)", "（未命名）" },
                { "{0} (resolves removal overlaps)", "{0}（用于解析移除区域重叠）" },
                { "none (0)", "无（0）" },
                { "Armatures", "骨架" },
                { "not selected", "未选择" },
                { "Schema", "架构" },
                { " (newer than this build)", "（高于本构建支持的版本）" },
                { "Signature", "签名" },
                { "captured and complete", "已捕获且完整" },
                { "captured but incomplete (APA024)", "已捕获但不完整（APA024）" },
                { "not captured (APA012)", "未捕获（APA012）" },
                { "Target Path", "目标路径" },
                { "(none)", "（无）" },
                { "Removal", "移除区域" },
                { "{0} triangle(s)", "{0} 个三角形" },
                { "none", "无" },
                { "Seam", "接缝" },
                { "Resolved Target", "解析到的目标" },
                { "(no recorded path)", "（没有记录的路径）" },
                { "(not found: {0})", "（未找到：{0}）" },
                { "This installer is disabled for build, so it contributes nothing and validation is skipped.",
                  "该安装器已停用构建，因此不产生任何贡献，验证也会被跳过。" },
                { "Shortcuts", "快捷操作" },
                { "Create Profile Asset…", "创建配置文件资产…" },
                { "Open Profile", "打开配置文件" },
                { "Edit In Part Authoring", "在部件制作窗口中编辑" },
                { "Clear Results", "清除结果" },
                { "This installer belongs to a prefab asset. Place the prefab under an avatar in a scene and " +
                  "select it there to validate geometry or to edit it in Part Authoring, so the part and the " +
                  "body share a space.",
                  "该安装器属于预制体资产。请把预制体放到场景中的角色下并在那里选中它，再验证几何体或在部件制作窗口中编辑，" +
                  "这样部件与身体才处于同一空间。" },
                { "No avatar root could be resolved from this installer.", "无法从该安装器解析出角色根对象。" },
                { "Validation could not build a context: {0}.", "验证无法构建上下文：{0}。" },
                { "Validation passed: {0}.", "验证通过：{0}。" },
                { "Validation failed: {0}.", "验证失败：{0}。" },
                { "Creating a profile needs an avatar root, a part root, and an explicit target body " +
                  "renderer on this installer. Assign the target renderer object, or use Part Authoring " +
                  "to pick the target body.",
                  "创建配置文件需要角色根对象、部件根对象，以及该安装器上明确指定的目标身体渲染器。" +
                  "请指定目标渲染器对象，或使用部件制作窗口选择目标身体。" },
                { "Creating the profile was blocked: the assembly cannot be planned. {0}. Nothing was written.",
                  "创建配置文件已被阻止：无法规划装配。{0}。未写入任何内容。" },
                { "Validation", "验证" },

                // ---- Bone fit (installer inspector) -----------------------------------------------------
                { "Bone Fit", "骨骼适配" },
                { "Bone fit needs the part placed in a scene under an avatar, so it is offered only for a " +
                  "scene installer.",
                  "骨骼适配需要部件已放置到场景中的角色层级下，因此只对场景中的安装器提供。" },
                { "Bone fit is unavailable in play mode.", "播放模式下无法使用骨骼适配。" },
                { "The part root could not be resolved, so bone fit is unavailable.",
                  "无法解析部件根对象，因此无法使用骨骼适配。" },
                { "This installer is not under an avatar, so there is no avatar pose to fit the part to. Place " +
                  "the part under an avatar in a scene.",
                  "该安装器不在任何角色层级下，因此没有可参照的角色姿势。请把部件放到场景中的角色下。" },
                { "The profile has no Armature selections yet, so the bones cannot be paired. Select both " +
                  "Armatures in Part Authoring (APA043).",
                  "配置文件尚未选择骨架，因此无法配对骨骼。请先在部件制作窗口中选择两个骨架（APA043）。" },
                { "The part armature recorded in the profile could not be resolved under the part root. " +
                  "Re-select the Part Armature in Part Authoring.",
                  "配置文件记录的部件骨架无法在部件根对象下解析。请在部件制作窗口中重新选择部件骨架。" },
                { "The target armature recorded in the profile could not be resolved under the avatar root. " +
                  "Re-select the Target Armature in Part Authoring.",
                  "配置文件记录的目标骨架无法在角色根对象下解析。请在部件制作窗口中重新选择目标骨架。" },
                { "Bone fit is unavailable ({0}).", "骨骼适配不可用（{0}）。" },
                { "Sync Part Bones To Avatar", "同步部件骨骼到 Avatar" },
                { "Follow Avatar Bones", "跟随 Avatar 骨骼" },
                { "Keep this part's bones on the avatar's current pose while the part is placed. Stored on this " +
                  "installer, so the choice survives closing the Inspector and a domain reload.",
                  "在放置部件期间，让部件骨骼保持与角色当前姿势一致。该选项保存在此安装器上，" +
                  "关闭检查器或重新加载域后依然保留。" },
                { "Include Scale", "包含缩放" },
                { "Also copy the avatar bones' scale onto the matching part bones. Needed when the avatar's " +
                  "bones have been rescaled.",
                  "同时把角色骨骼的缩放复制到对应的部件骨骼。当角色骨骼被重新缩放时需要此项。" },
                { "No part bone matched a same-named avatar bone, so there is nothing to align. Check the " +
                  "two Armature selections in Part Authoring.",
                  "没有部件骨骼匹配到同名的 Avatar 骨骼，因此没有可对齐的内容。请在部件制作窗口中检查两个骨架选择。" },
                { "Aligned {0} part bone(s) to the avatar's bones.",
                  "已将 {0} 根部件骨骼对齐到 Avatar 的骨骼。" },
                { "Aligned {0} part bone(s); {1} had no same-named avatar bone and stayed in place: {2}",
                  "已对齐 {0} 根部件骨骼；{1} 根没有同名的 Avatar 骨骼，保持原位：{2}" },
                { "… and {0} more unmatched bone(s)", "… 另有 {0} 根未匹配的骨骼" },
                { "While following, a moved or scaled avatar bone moves the matching part bones with it every " +
                  "editor update. The writes are not undoable — turn the toggle off to keep the pose as it is. " +
                  "The bone pairs are captured when following turns on, so toggle it off and on again after " +
                  "changing the Armature selections or the part.",
                  "跟随开启时，Avatar 骨骼的每次移动或缩放都会在编辑器更新时带动对应的部件骨骼。" +
                  "这些写入无法撤销——关闭开关即可保留当前姿势。骨骼配对在开启跟随时捕获，" +
                  "因此更改骨架选择或部件后，请先关闭再重新开启跟随。" },
                { "Modular Avatar's armature lock is active on {0} merge configuration(s) bundled with this " +
                  "part ({1}). It runs in the edit scene and snaps bones back while you pose them — that is " +
                  "the pull-back you are seeing, not the merge preview. APA generates its own merge " +
                  "configuration at build time, so these are not needed here.",
                  "Modular Avatar 的骨骼锁定正在该部件自带的 {0} 个合并配置上生效（{1}）。" +
                  "它在编辑场景中运行，会在你摆姿势时把骨骼拉回——你看到的就是它，而不是合并预览。" +
                  "APA 会在构建时生成自己的合并配置，因此这里不需要它们。" },
                { "Modular Avatar's armature lock is active on {0} merge configuration(s) elsewhere in the " +
                  "avatar hierarchy ({1}). Its lock also snaps avatar bones while you pose; if bones still " +
                  "pull back after this part's own locks are disabled, these are the cause.",
                  "Modular Avatar 的骨骼锁定正在角色层级中其他 {0} 个合并配置上生效（{1}）。" +
                  "它们的锁定同样会在摆姿势时吸附 Avatar 骨骼；如果禁用本部件自身的锁定后骨骼仍被拉回，原因就是它们。" },
                { "Set Lock Mode To Not Locked", "锁定模式改为未锁定" },
                { "Set {0} merge configuration(s) to Not Locked.", "已将 {0} 个合并配置改为未锁定。" },
                { "… and {0} more merge configuration(s)", "… 另有 {0} 个合并配置" },

                // ---- Removal mask and seam selection descriptions --------------------------------------
                { "corrupt storage (mismatched arrays)", "存储损坏（数组长度不匹配）" },
                { "no triangles removed", "未移除任何三角形" },
                { "{0} triangle(s) in {1} submesh(es)", "{1} 个子网格中共 {0} 个三角形" },

                // ---- Authoring validation descriptions --------------------------------------------------
                { "no plan", "没有规划" },
                { "{0} output vertex(es), {1} triangle(s), {2} submesh(es), {3} UV channel(s), " +
                  "{4} removed triangle(s)",
                  "{0} 个输出顶点、{1} 个三角形、{2} 个子网格、{3} 个 UV 通道、{4} 个移除三角形" },

                // ---- Compatibility capture descriptions -------------------------------------------------
                { "signature", "签名" },
                { "capture flag", "捕获标记" },
                { "vertex count", "顶点数" },
                { "submesh index counts", "子网格索引数量" },
                { "submesh topologies", "子网格拓扑" },
                { "blend shape frame counts", "形态键帧数" },
                { "no signature", "没有签名" },
                { "path='{0}'; mesh='{1}'; {2}", "路径='{0}'；网格='{1}'；{2}" },

                // ---- Asset path rejection sentences ----------------------------------------------------
                { "The path is empty.", "路径为空。" },
                { "It is an absolute filesystem path rather than a project-relative asset path.",
                  "它是文件系统的绝对路径，而不是项目相对路径。" },
                { "It contains a '..' segment, which could resolve outside the project.",
                  "它包含 '..' 区段，可能解析到项目之外。" },
                { "It does not start with 'Assets/'.", "它不以 'Assets/' 开头。" },
                { "It names a folder rather than a file.", "它指向的是文件夹而不是文件。" },
                { "It contains a character that is not allowed in an asset file name.",
                  "它包含资产文件名不允许的字符。" },
                { "It does not end with '{0}'.", "它不以 '{0}' 结尾。" },
                { "It is not a usable asset path.", "它不是可用的资产路径。" },

                // ---- Profile writer operation statuses -------------------------------------------------
                { "No profile draft was supplied.", "没有提供配置草稿。" },
                { "The profile was not written because the draft is not valid: {0}.",
                  "草稿无效，因此未写入配置文件：{0}。" },
                { "The path '{0}' is occupied by '{1}' ({2}). Nothing was written. Choose a path that is not " +
                  "already in use.",
                  "路径 '{0}' 已被 '{1}'（{2}）占用。未写入任何内容。请选择一个尚未被使用的路径。" },
                { "A profile already exists at '{0}'. Nothing was written. Confirm the replacement explicitly " +
                  "to update it.",
                  "'{0}' 处已存在配置文件。未写入任何内容。请明确确认替换后才能更新。" },
                { "Created profile '{0}'.", "已创建配置文件 '{0}'。" },
                { "Updated profile '{0}'.", "已更新配置文件 '{0}'。" },
                { "Writing the profile failed: {0}", "写入配置文件失败：{0}" },

                // ---- Prefab generator operation statuses ----------------------------------------------
                { "A prefab already exists at '{0}'. Nothing was written. Confirm the replacement explicitly " +
                  "to overwrite it, or use 'Update installer on prefab' to keep the existing content.",
                  "'{0}' 处已存在预制体。未写入任何内容。请明确确认替换才能覆盖，" +
                  "或改用“更新预制体上的安装器”以保留已有内容。" },
                { "Nothing was written and the existing prefab at '{0}' is untouched: the reference scan " +
                  "stopped at its property budget after {1} properties, so the scene part cannot be proven " +
                  "portable.",
                  "未写入任何内容，'{0}' 处已有的预制体保持不变：引用扫描在检查了 {1} 个属性后达到预算上限，" +
                  "因此无法证明场景中的部件是可移植的。" },
                { " It had already found {0} non-portable reference(s).", " 它已发现 {0} 个不可移植的引用。" },
                { " (it had already found {0} non-portable reference(s))",
                  "（它已发现 {0} 个不可移植的引用）" },
                { "Nothing was written and the existing prefab at '{0}' is untouched: the scene part contains " +
                  "{1} reference(s) that cannot be stored in an asset.",
                  "未写入任何内容，'{0}' 处已有的预制体保持不变：场景中的部件包含 {1} 个无法存入资产的引用。" },
                { "The scene part root is a prefab instance, so the new prefab was built from the instance's " +
                  "current scene state as an independent prefab, not as a variant of the source prefab.",
                  "场景中的部件根对象是一个预制体实例，因此新预制体是按其实时场景状态生成的独立预制体，" +
                  "而不是源预制体的变体。" },
                { "Unity could not save '{0}' as a prefab at '{1}'.", "Unity 无法将 '{0}' 保存为 '{1}' 处的预制体。" },
                { "The prefab at '{0}' could not be loaded for configuration.", "无法加载 '{0}' 处的预制体进行配置。" },
                { "The existing prefab at '{0}' was replaced with the scene part, but the written prefab could " +
                  "not be loaded to configure its installer. The prefab is present but unconfigured; restore " +
                  "it from version control if that content mattered.",
                  "'{0}' 处已有的预制体已被场景中的部件替换，但无法加载写入后的预制体来配置其安装器。" +
                  "该预制体存在但未配置；如果那份内容重要，请从版本控制恢复。" },
                { "the reference scan stopped at its property budget after {0} properties, so the prefab " +
                  "cannot be proven portable",
                  "引用扫描在检查了 {0} 个属性后达到预算上限，因此无法证明该预制体是可移植的" },
                { "the reference scan stopped at its property budget after {0} properties, so the loaded " +
                  "prefab cannot be proven portable",
                  "引用扫描在检查了 {0} 个属性后达到预算上限，因此无法证明加载的预制体是可移植的" },
                { "it contains {0} reference(s) that cannot be stored in an asset",
                  "它包含 {0} 个无法存入资产的引用" },
                { "The prefab was not written: {0}.", "未写入预制体：{0}。" },
                { "The existing prefab at '{0}' was replaced with the scene part, but its installer could not " +
                  "be configured because {1}. The prefab is present but unconfigured; restore it from version " +
                  "control if that content mattered.",
                  "'{0}' 处已有的预制体已被场景中的部件替换，但由于{1}，其安装器无法配置。" +
                  "该预制体存在但未配置；如果那份内容重要，请从版本控制恢复。" },
                { "Created prefab '{0}'.", "已创建预制体 '{0}'。" },
                { "Replaced prefab '{0}'.", "已替换预制体 '{0}'。" },
                { "Generating the prefab failed: {0}", "生成预制体失败：{0}" },
                { "The path '{0}' is occupied by '{1}' ({2}), not a prefab. Nothing was written. Choose a path " +
                  "that is not already in use.",
                  "路径 '{0}' 已被 '{1}'（{2}）占用，且不是预制体。未写入任何内容。请选择一个尚未被使用的路径。" },
                { "No prefab exists at '{0}'. Create it first.", "'{0}' 处不存在预制体。请先创建它。" },
                { "The prefab was not updated: {0}.", "未更新预制体：{0}。" },
                { "Updated the installer on '{0}'.", "已更新 '{0}' 上的安装器。" },
                { "Updating the prefab failed: {0}", "更新预制体失败：{0}" },
                { "Added an AvatarPartInstaller to the prefab root '{0}'.",
                  "已向预制体根对象 '{0}' 添加 AvatarPartInstaller。" },
                { "Cleared the installer's explicit target renderer assignment ('{0}'), which pointed outside " +
                  "the prefab. The target is resolved through the profile's captured renderer path instead, " +
                  "which is what makes the prefab portable.",
                  "已清除安装器上指向预制体外部的显式目标渲染器指定（'{0}'）。" +
                  "目标改为通过配置文件捕获的渲染器路径解析，这正是预制体可移植的原因。" },

                // ---- Preview diagnostics and overlay ---------------------------------------------------
                { "Preview blocked", "预览已阻止" },
                { "Preview active", "预览生效" },
                { " — {0} ERROR", " — {0} 个错误" },
                { " — {0} WARNING", " — {0} 个警告" },
                { " — {0} INFO", " — {0} 条信息" },
                { "APA preview: {0}", "APA 预览：{0}" },
                { "\nNOTE: {0}", "\n注意：{0}" },
                { "\n… {0} more", "\n… 另有 {0} 项" },
                { "APA preview: {0} removed tri, {1} seam match", "APA 预览：{0} 个移除三角形，{1} 个接缝匹配" },
                { ", {0} unresolved bone(s)", "，{0} 根未解析的骨骼" },
                { "\nfingerprint {0}", "\n指纹 {0}" },
                { "debug overlay failed: {0}", "调试叠加层失败：{0}" },
                { "preview", "预览" },
                { "APA seam/removal overlay", "APA 接缝/移除叠加层" },
                { "(no avatar)", "（无角色）" },
                { "scene {0} object {1}", "场景 {0} 对象 {1}" },

                // ---- NDMF pass names -------------------------------------------------------------------
                { "Create transient merge-armature configuration", "创建临时合并骨架配置" },
                { "Assemble part geometry into the target body mesh", "将部件几何体装配到目标身体网格" },
                { "Retarget consumed part animation onto the target renderer",
                  "将已消费部件的动画重定向到目标渲染器" },
                { "Remove consumed part objects left empty", "移除已消费且变空的部件对象" },
                { "Restore protected part meshes in memory", "在内存中还原受保护部件网格" },
                { "Release transient protected part meshes", "释放临时受保护部件网格" }
            };

        /// <summary>
        /// One short Simplified Chinese description per allocated <c>APAxxx</c> code.
        /// </summary>
        /// <remarks>
        /// The description is appended to the rendered diagnostic beside the unchanged code and English mnemonic
        /// title, so the stable token stays searchable and the Chinese reader gets a one-line explanation. A code
        /// with no entry renders exactly as it always did, which is the fail-soft direction: a future code is
        /// still visible, just not yet described.
        /// </remarks>
        internal static readonly Dictionary<string, string> ErrorCodeDescriptions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "APA001", "基础与部件的接缝顶点数量不一致" },
                { "APA002", "部件接缝顶点在容差内找不到对应的基础顶点" },
                { "APA003", "一个部件接缝顶点在容差内匹配到多个基础顶点" },
                { "APA004", "同名 UV 语义在焊接接缝顶点上取值不一致" },
                { "APA005", "合并后 UV 通道超过 8 个" },
                { "APA006", "无法解析目标渲染器或其网格" },
                { "APA007", "无法解析所需的最终骨骼" },
                { "APA008", "骨骼层级冲突：骨骼缺少身份，或同一身份对应多根骨骼" },
                { "APA009", "两个不同材质资产在同一语义上冲突且无法消解" },
                { "APA010", "两个部件声明移除同一个基础三角形" },
                { "APA011", "无法生成有效的绑定姿势" },
                { "APA012", "配置文件与解析到的目标渲染器或网格不兼容" },
                { "APA013", "两个非自定义槽位的部件冲突" },
                { "APA014", "输入网格包含本构建无法保留的数据" },
                { "APA015", "配置文件架构版本高于本构建支持的版本" },
                { "APA016", "输入值不是有限数值" },
                { "APA017", "移除三角形索引超出目标子网格的范围" },
                { "APA018", "接缝索引重复、越界或不是有效的顶点集合" },
                { "APA019", "同一来源重复声明了 UV 或材质语义" },
                { "APA020", "语义名称为空或只有空白字符" },
                { "APA021", "重映射产生了退化的三角形" },
                { "APA022", "配置的容差不是正的有限数" },
                { "APA023", "部件未声明槽位，或槽位与其配置文件不一致" },
                { "APA024", "捕获的目标签名缺少本构建所需的校验数据" },
                { "APA025", "多个部件为同一焊接顶点写入了不同的 UV 值" },
                { "APA026", "移除地址本身无法解析" },
                { "APA027", "同一网格内出现重名的形态键" },
                { "APA028", "基础与部件的同名形态键在帧数或帧权重上不一致" },
                { "APA029", "形态键在焊接接缝顶点上的增量无法保留" },
                { "APA030", "形态键某一帧的增量数组无法按顶点索引" },
                { "APA031", "骨骼权重不可用：数量不匹配、为负，或权重和为零" },
                { "APA032", "空间变换缺失、非有限或不可逆" },
                { "APA033", "输出路径无法指向 Unity 资产" },
                { "APA034", "必须序列化的引用指向了场景对象" },
                { "APA035", "移除区域重叠已按冲突优先级判定归属" },
                { "APA036", "网格中存在未声明的 UV 通道" },
                { "APA037", "冲突优先级取值超出定义域" },
                { "APA038", "某个来源子网格没有对应的最终材质槽位" },
                { "APA039", "安装器未参与构建，已跳过" },
                { "APA040", "部件独立形态键被策略拒绝" },
                { "APA041", "黑白遮罩无法转换为移除三角形选择" },
                { "APA042", "接缝是在显式配对之前制作的，需按世界坐标重新生成" },
                { "APA043", "骨架选择不可用：未选择、路径无法解析，或所选对象不在对应根下" },
                { "APA044", "参与加权的骨骼不在该来源所选的骨架内" },
                { "APA045", "同名 UV 语义在接缝配对上不一致，已保留部件接缝顶点（拆分顶点）" },
                { "APA046", "配置文件没有保存部件 ID，已从配置文件资产 GUID 派生稳定 ID" },
                { "APA050", "UV 语义声明的通道在部件网格上不存在" },
                { "APA051", "（已退役）无法把渲染器上名为 'merge vertex' 的顶点组解析为候选顶点：没有声明该组、组内为空、" +
                            "权重全为零、同名骨骼不唯一，或列出的索引无法用于该网格。该表示已被顶点颜色候选契约取代，本构建不再产生此码" },
                { "APA052", "无法把渲染器的顶点颜色解析为接缝候选顶点：网格没有顶点颜色、颜色数量与顶点数不一致，" +
                            "或没有任何顶点带有选中的候选颜色" },
                { "APA999", "装配器内部发生了未预期的异常" }
            };
    }
}
