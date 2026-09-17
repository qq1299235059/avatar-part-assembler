using System;
using AvatarPartAssembler.Editor.Localization;
using nadena.dev.ndmf.preview;
using UnityEditor;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// The preview switches APA exposes in NDMF's <i>Configure Previews</i> window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both switches are <c>TogglablePreviewNode</c>s with a qualified name, so NDMF persists the user's choice
    /// and lists them under the owning plugin. The debug overlay starts <c>false</c>: section 29's Scene View
    /// display is a diagnostic aid, and a preview that paints extra geometry by default would misrepresent the
    /// result it is supposed to be previewing.
    /// </para>
    /// <para>
    /// The nodes are created eagerly from <see cref="InitializeOnLoadMethodAttribute"/> so that NDMF's preference
    /// object exists before the first preview session is built, and so a saved "off" state is loaded before any
    /// filter asks for it.
    /// </para>
    /// </remarks>
    public static class ApaPreviewToggles
    {
        /// <summary>
        /// Product name shown for the preview switches.
        /// </summary>
        /// <remarks>
        /// The product name is the same in both languages, so this entry resolves to itself; it is written as a
        /// lookup anyway so that a future localized product name has one place to change.
        /// </remarks>
        private const string ProductName = "Avatar Part Assembler";

        /// <summary>
        /// Master switch for the assembled-body preview.
        /// </summary>
        /// <remarks>
        /// The title is a lambda so that NDMF's <i>Configure Previews</i> window shows the name in the language
        /// that is selected when the window draws, rather than the one that was selected when the domain loaded.
        /// </remarks>
        public static TogglablePreviewNode MainPreview { get; } = TogglablePreviewNode.Create(
            () => ApaLocalization.Tr(ProductName),
            qualifiedName: "dev.avatar-part-assembler/preview/Main",
            initialState: true);

        /// <summary>Switch for the seam/removal Scene View overlay. Off by default.</summary>
        public static TogglablePreviewNode DebugOverlay { get; } = TogglablePreviewNode.Create(
            () => ApaLocalization.Tr("APA seam/removal overlay"),
            qualifiedName: "dev.avatar-part-assembler/preview/DebugOverlay",
            initialState: false);

        /// <summary>Forces the static initializers above to run when the editor loads.</summary>
        [InitializeOnLoadMethod]
        private static void Touch()
        {
            // The body is intentionally empty: reading the properties is what initializes the class, and doing it
            // here means the saved toggle state is applied at load rather than at first preview.
            _ = MainPreview;
            _ = DebugOverlay;
        }
    }

    /// <summary>
    /// The seam through which the M4 preview filter is published to a preview session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NDMF discovers render filters through a build pass: a plugin declares
    /// <c>seq.Run(ApaAssemblyPass.Instance).PreviewingWith(filter)</c>, and NDMF's plugin resolver collects the
    /// filters of every enabled pass into a <c>PreviewSession</c>. That declaration lives in
    /// <c>dev.avatar-part-assembler.editor.ndmf</c>, which is the only assembly that may reference both this
    /// preview assembly and NDMF.
    /// </para>
    /// <para>
    /// The registration must live in an assembly that this preview assembly does <b>not</b> reference: adding
    /// <c>dev.avatar-part-assembler.editor.preview</c> to the root editor assembly while the preview assembly
    /// references the root editor assembly would make the two assembly definitions cyclic, which Unity does not
    /// support, and the same holds for a reference from here back to
    /// <c>dev.avatar-part-assembler.editor.ndmf</c>. See <see cref="ApaPreviewRegistration"/> for the exact
    /// shape.
    /// </para>
    /// <para>
    /// This assembly keeps its reference to <c>nadena.dev.ndmf</c> itself, which is what makes the NDMF preview
    /// types (<c>IRenderFilter</c>, <c>ComputeContext</c>, <c>TogglablePreviewNode</c>) nameable here. "Preview
    /// must not reference NDMF" means the Avatar Part Assembler NDMF assembly, not the NDMF package: a render
    /// filter <i>is</i> an NDMF type.
    /// </para>
    /// </remarks>
    public interface IApaPreviewRegistrationHost
    {
        /// <summary>True when a session exists that can accept a filter right now.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Registers a filter and returns a handle that removes it again.
        /// </summary>
        /// <remarks>
        /// Each call must be given a fresh filter instance: NDMF rejects a second registration of the same
        /// instance within one session. The returned handle must be safe to dispose more than once.
        /// </remarks>
        IDisposable Register(IRenderFilter filter);
    }

    /// <summary>
    /// The default <see cref="IApaPreviewRegistrationHost"/>: NDMF's current preview session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a test seam, not a production registration path.</b> NDMF replaces and disposes
    /// <c>PreviewSession.Current</c> whenever the preview configuration changes, and a mutator added to whichever
    /// session happened to be current is lost with it. A filter registered this way is also invisible to
    /// <i>Tools/NDM Framework/Configure Previews</i>, which only walks the render filters of passes. Production
    /// registration is the <c>PreviewingWith</c> declaration described on <see cref="ApaPreviewRegistration"/>.
    /// </para>
    /// <para>
    /// The session accessor is injectable so that a test can supply a session-like host without touching the
    /// global <c>PreviewSession.Current</c> that the running editor owns.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewSessionHost : IApaPreviewRegistrationHost
    {
        private readonly Func<PreviewSession> _session;

        /// <summary>Creates a host over <c>PreviewSession.Current</c>.</summary>
        public ApaPreviewSessionHost()
            : this(() => PreviewSession.Current)
        {
        }

        /// <summary>Creates a host over an explicit session accessor.</summary>
        public ApaPreviewSessionHost(Func<PreviewSession> session)
        {
            _session = session;
        }

        /// <inheritdoc />
        public bool IsAvailable
        {
            get
            {
                var session = _session != null ? _session() : null;
                return session != null;
            }
        }

        /// <inheritdoc />
        public IDisposable Register(IRenderFilter filter)
        {
            var session = _session != null ? _session() : null;
            if (session == null || filter == null) return ApaNoOpDisposable.Instance;

            return session.AddMutator(new SequencePoint(), filter);
        }
    }

    /// <summary>
    /// The preview filter factory the NDMF integration calls, plus the registration it declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass that owns mesh assembly publishes this filter, in
    /// <c>dev.avatar-part-assembler.editor.ndmf/ApaNdmfPlugin.Configure</c>:
    /// </para>
    /// <code>
    /// InPhase(BuildPhase.Transforming)
    ///     .AfterPlugin("nadena.dev.modular-avatar")
    ///     .Run(ApaAssemblyPass.Instance)
    ///     .PreviewingWith(ApaPreviewRegistration.CreateFilter());
    /// </code>
    /// <para>
    /// The call lives in an assembly that the preview assembly does not reference —
    /// <c>dev.avatar-part-assembler.editor.ndmf</c>, which references <c>dev.avatar-part-assembler.editor</c>,
    /// <c>dev.avatar-part-assembler.editor.preview</c> and <c>nadena.dev.ndmf</c>. It must <b>not</b> be added to
    /// the root editor assembly definition: that assembly is already referenced by this preview assembly, so
    /// adding the reverse reference would create a cycle, which Unity's script compilation does not support.
    /// </para>
    /// <para>
    /// Filter order follows pass order, so the preview runs the pass the build runs rather than a copy of it, and
    /// NDMF's <i>Configure Previews</i> window lists this filter under
    /// <see cref="ApaPreviewRegistration.PluginQualifiedName"/>.
    /// </para>
    /// </remarks>
    public static class ApaPreviewRegistration
    {
        /// <summary>Qualified name of the plugin the preview filter is registered under.</summary>
        public const string PluginQualifiedName = "dev.avatar-part-assembler";

        /// <summary>Human-readable description of the registration this package declares.</summary>
        public const string RequiredPassRegistration =
            "seq.Run(ApaAssemblyPass.Instance).PreviewingWith(ApaPreviewRegistration.CreateFilter());";

        /// <summary>
        /// Creates a filter instance. A new instance per registration, because NDMF rejects registering the same
        /// instance twice in one session.
        /// </summary>
        public static IRenderFilter CreateFilter()
        {
            return new AvatarPartRenderFilter();
        }

        /// <summary>
        /// Registers a fresh filter through a host and returns the removal handle. A null or unavailable host is
        /// tolerated so that a caller in a build context cannot fail on registration.
        /// </summary>
        /// <remarks>
        /// This is the session-host path, which exists for tests (see <see cref="ApaPreviewSessionHost"/>); the
        /// production registration is the pass declaration described above, not a call to this method.
        /// </remarks>
        public static IDisposable Register(IApaPreviewRegistrationHost host)
        {
            if (host == null) return ApaNoOpDisposable.Instance;
            return host.Register(CreateFilter());
        }
    }

    /// <summary>A disposable that does nothing; used when there is no session to unregister from.</summary>
    internal sealed class ApaNoOpDisposable : IDisposable
    {
        internal static readonly ApaNoOpDisposable Instance = new ApaNoOpDisposable();

        private ApaNoOpDisposable()
        {
        }

        public void Dispose()
        {
        }
    }
}
