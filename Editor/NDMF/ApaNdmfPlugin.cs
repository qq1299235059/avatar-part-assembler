using AvatarPartAssembler.Editor.Localization;
using AvatarPartAssembler.Editor.Preview;
using nadena.dev.ndmf;
using nadena.dev.ndmf.fluent;

[assembly: ExportsPlugin(typeof(AvatarPartAssembler.Editor.Ndmf.ApaNdmfPlugin))]

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Registers the Avatar Part Assembler build passes with NDMF and declares their order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass layout follows section 24 of the specification and the installed NDMF API:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Generating</b> creates the transient Modular Avatar merge-armature configuration. NDMF runs
    /// <see cref="BuildPhase.Generating"/> entirely before <see cref="BuildPhase.Transforming"/>
    /// (<c>BuildPhase.BuiltInPhases</c>), and Modular Avatar performs its merges in Transforming, so the
    /// configuration exists before Modular Avatar reads it.
    /// </description></item>
    /// <item><description>
    /// <b>Transforming</b> assembles the geometry, after Modular Avatar. The constraint is declared by qualified
    /// name because Modular Avatar's plugin class is internal to its own assembly; the name is the one it
    /// declares: <c>PluginDefinition.QualifiedName = "nadena.dev.modular-avatar"</c>
    /// (Editor/PluginDefinition/PluginDefinition.cs). NDMF constraints are phase-local and missing targets are
    /// optional (<c>PluginResolver</c> skips a constraint whose endpoints do not exist), so this constraint
    /// orders the pass against Modular Avatar's own Transforming passes when Modular Avatar is present and does
    /// nothing when it is not.
    /// </description></item>
    /// <item><description>
    /// <b>Before the optimizers</b> is expressed by the phase, not by a constraint. NDMF sorts passes inside a
    /// phase and then emits the phases in <c>BuildPhase.BuiltInPhases</c> order, so every Transforming pass runs
    /// before every Optimizing pass; <c>BeforePlugin</c> cannot express it because a constraint between passes of
    /// different phases is rejected by <c>PluginResolver</c>, and a constraint against an optimizer that is not
    /// installed would only add phantom passes. The assembly pass therefore stays in Transforming, ahead of
    /// Avatar Optimizer and anything else that optimizes the finished mesh.
    /// </description></item>
    /// <item><description>
    /// <b>Preview registration is part of this pass declaration, not a second processor.</b> M4's preview filter
    /// is attached to the Transforming pass with
    /// <c>seq.Run(ApaAssemblyPass.Instance).PreviewingWith(ApaPreviewRegistration.CreateFilter())</c>, so preview
    /// and build run one ordered pass and NDMF's <i>Configure Previews</i> window lists the filter under this
    /// plugin. The filter is a fresh instance per registration (filter order follows pass order; one fresh filter
    /// instance per registration). <c>dev.avatar-part-assembler.editor.ndmf</c> references
    /// <c>dev.avatar-part-assembler.editor.preview</c> so the filter type is nameable here, and
    /// <c>dev.avatar-part-assembler.editor.preview</c> carries no reference back to this assembly — the reverse
    /// reference is an assembly cycle, so the registration lives in an assembly Preview does not reference.
    /// <see cref="ApaPreviewRegistration.CreateFilter"/> takes no arguments and returns a self-contained filter,
    /// so it does not call <see cref="ApaBuildProcessor"/> by name; preview and build share the processor's
    /// contract instead — the same discovery predicate, the same target-group planning, and the same
    /// per-group assembly entry point (see <c>ApaPreviewDiscovery</c> and <c>ApaPartConsumptionPlanner</c>).
    /// </description></item>
    /// </list>
    /// <para>
    /// Both passes are VRChat-avatar-only by default, which is NDMF's documented default for a plugin without
    /// <c>RunsOnAllPlatforms</c> (<c>PluginInfo</c> defaults the platform filter to
    /// <c>WellKnownPlatforms.VRChatAvatar30</c>). The plugin's declared dependency is the VRChat SDK, so that is
    /// the platform it is built for.
    /// </para>
    /// </remarks>
    internal sealed class ApaNdmfPlugin : Plugin<ApaNdmfPlugin>
    {
        /// <summary>Qualified name of this plugin, as NDMF reports it and other plugins constrain against it.</summary>
        public const string PluginQualifiedName = "dev.avatar-part-assembler";

        /// <summary>
        /// Qualified name of Modular Avatar's plugin. A string constant rather than a type reference because
        /// Modular Avatar's plugin class is internal to <c>nadena.dev.modular-avatar.core.editor</c>.
        /// </summary>
        public const string ModularAvatarPluginQualifiedName = "nadena.dev.modular-avatar";

        /// <inheritdoc />
        public override string QualifiedName => PluginQualifiedName;

        /// <inheritdoc />
        /// <remarks>
        /// The display name is resolved when NDMF draws it, so the plugin's name in the build report and in
        /// <i>Configure Previews</i> follows the selected language. The qualified name is unchanged, which is what
        /// other plugins constrain against.
        /// </remarks>
        public override string DisplayName => ApaLocalization.Tr("Avatar Part Assembler");

        /// <inheritdoc />
        protected override void Configure()
        {
            InPhase(BuildPhase.Generating)
                .Run(ApaMergeArmaturePass.Instance);

            InPhase(BuildPhase.Transforming)
                .AfterPlugin(ModularAvatarPluginQualifiedName)
                .Run(ApaAssemblyPass.Instance)

                // Preview registration. NDMF discovers render filters through the pass that owns the work, so
                // attaching the filter to this pass is what makes the Scene View preview run the same ordered
                // pass the build runs instead of a second, parallel processor. The call is made here, in
                // dev.avatar-part-assembler.editor.ndmf, because that is the only assembly that may reference
                // both this package's preview assembly and NDMF; the reverse reference (preview -> this assembly)
                // would be an assembly cycle, which Unity does not support.
                //
                // A fresh filter instance per registration: NDMF rejects a second registration of the same
                // instance within one session (PreviewSession.AddMutator), and Configure runs again on every
                // domain reload.
                .PreviewingWith(ApaPreviewRegistration.CreateFilter());
        }
    }
}
