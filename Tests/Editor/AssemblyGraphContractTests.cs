using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Locks the package's assembly graph: the asmdefs this package owns, the references they declare, and the
    /// direction of every edge between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The graph is a compile-time contract, and every failure mode it has is silent at author time: a nested
    /// asmdef that stops excluding its sources, a reference added in the wrong direction (an assembly cycle,
    /// which Unity cannot build at all), or a test assembly that loses the reference it needs to see the NDMF
    /// types. These assertions are read from the asmdef files on disk, which is the same source the offline
    /// compile check drives, so a contract test and a real compilation cannot disagree.
    /// </para>
    /// <para>
    /// The edges (<c>A -&gt; B</c> means "A declares a reference to B"):
    /// <c>editor -&gt; runtime</c>, <c>preview -&gt; editor</c>, <c>ndmf -&gt; preview</c>,
    /// <c>tests -&gt; {runtime, editor, preview, ndmf}</c>. Preview's declared reference to the
    /// <c>nadena.dev.ndmf</c> <i>package</i> is required — a render filter implements
    /// <c>nadena.dev.ndmf.preview.IRenderFilter</c> — and is not an edge to this package's
    /// <c>dev.avatar-part-assembler.editor.ndmf</c> assembly, which is what would be a cycle.
    /// </para>
    /// </remarks>
    public sealed class AssemblyGraphContractTests
    {
        private const string Runtime = "dev.avatar-part-assembler.runtime";
        private const string Editor = "dev.avatar-part-assembler.editor";
        private const string Preview = "dev.avatar-part-assembler.editor.preview";
        private const string Ndmf = "dev.avatar-part-assembler.editor.ndmf";
        private const string Tests = "dev.avatar-part-assembler.tests.editor";

        /// <summary>The asmdef of an assembly this package owns, relative to the package root.</summary>
        private static readonly Dictionary<string, string> s_paths = new Dictionary<string, string>
        {
            { Runtime, "Runtime/dev.avatar-part-assembler.runtime.asmdef" },
            { Editor, "Editor/dev.avatar-part-assembler.editor.asmdef" },
            { Preview, "Editor/Preview/dev.avatar-part-assembler.editor.preview.asmdef" },
            { Ndmf, "Editor/NDMF/dev.avatar-part-assembler.editor.ndmf.asmdef" },
            { Tests, "Tests/Editor/dev.avatar-part-assembler.tests.editor.asmdef" }
        };

        /// <summary>The references each asmdef must declare, exactly.</summary>
        [Test]
        public void EveryAsmdefDeclaresItsContractReferences()
        {
            CollectionAssert.AreEquivalent(
                new[] { Runtime, "nadena.dev.modular-avatar.core" },
                ReferencesOf(Editor),
                "The core editor assembly builds against the runtime and Modular Avatar.");

            CollectionAssert.AreEquivalent(
                new[] { Runtime, Editor, "nadena.dev.ndmf" },
                ReferencesOf(Preview),
                "Preview needs the NDMF package types it implements, and this package's core.");

            CollectionAssert.AreEquivalent(
                new[] { Runtime, Editor, Preview, "nadena.dev.ndmf" },
                ReferencesOf(Ndmf),
                "The NDMF assembly is the only one that may see both the preview filter and NDMF.");

            CollectionAssert.AreEquivalent(
                new[] { Runtime, Editor, Preview, Ndmf, "nadena.dev.ndmf", "UnityEngine.TestRunner", "UnityEditor.TestRunner" },
                ReferencesOf(Tests),
                "The tests drive the preview, the NDMF processor and the core directly.");
        }

        /// <summary>NDMF references Preview; Preview must never reference NDMF back.</summary>
        [Test]
        public void NdmfReferencesPreviewAndPreviewDoesNotReferenceNdmf()
        {
            CollectionAssert.Contains(ReferencesOf(Ndmf), Preview);
            CollectionAssert.DoesNotContain(
                ReferencesOf(Preview),
                Ndmf,
                "preview -> editor.ndmf is an assembly cycle: the registration belongs in the NDMF assembly.");
        }

        /// <summary>The tests reference both the preview and the NDMF assemblies, so both are directly testable.</summary>
        [Test]
        public void TestsReferencePreviewAndNdmf()
        {
            CollectionAssert.Contains(ReferencesOf(Tests), Preview);
            CollectionAssert.Contains(ReferencesOf(Tests), Ndmf);
        }

        /// <summary>No package assembly can reach itself through the declared references: the graph is acyclic.</summary>
        [Test]
        public void PackageAssemblyGraphIsAcyclic()
        {
            var graph = new Dictionary<string, string[]>();
            foreach (var pair in s_paths) graph[pair.Key] = ReferencesOf(pair.Key);

            // Depth-first walk with a "visiting" set: an edge back into a node on the current path is a cycle.
            var done = new HashSet<string>();
            var path = new List<string>();

            foreach (var root in s_paths.Keys)
            {
                Visit(root, graph, done, path);
            }

            Assert.IsEmpty(path, "A reference cycle would make the package unbuildable: " + string.Join(" -> ", path));
        }

        private static void Visit(
            string name,
            Dictionary<string, string[]> graph,
            HashSet<string> done,
            List<string> path)
        {
            if (done.Contains(name)) return;
            if (path.Contains(name))
            {
                path.Add(name);
                Assert.Fail("assembly reference cycle: " + string.Join(" -> ", path));
            }

            path.Add(name);
            foreach (var reference in graph[name])
            {
                if (graph.ContainsKey(reference)) Visit(reference, graph, done, path);
            }

            path.RemoveAt(path.Count - 1);
            done.Add(name);
        }

        /// <summary>A nested asmdef owns its folder: its sources are not compiled into the parent assembly.</summary>
        [Test]
        public void NestedAsmdefsOwnTheirSourceFolders()
        {
            var packageRoot = PackageRoot();
            var editorRoot = Path.Combine(packageRoot, "Editor");

            var nested = new List<string>();
            foreach (var file in Directory.GetFiles(editorRoot, "*.asmdef", SearchOption.AllDirectories))
            {
                var directory = Path.GetDirectoryName(file);
                if (!string.Equals(directory, editorRoot, StringComparison.OrdinalIgnoreCase))
                {
                    nested.Add(directory);
                }
            }

            CollectionAssert.AreEquivalent(
                new[]
                {
                    Path.Combine(editorRoot, "NDMF"),
                    Path.Combine(editorRoot, "Preview")
                },
                nested,
                "Editor/NDMF and Editor/Preview are separate assemblies; the core editor assembly must not own them.");
        }

        /// <summary>The declared references of one package assembly, as the asmdef on disk declares them.</summary>
        private static string[] ReferencesOf(string assemblyName)
        {
            var path = Path.Combine(PackageRoot(), s_paths[assemblyName].Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), "Missing asmdef: " + path);

            var text = File.ReadAllText(path);
            Assert.IsTrue(
                text.Contains("\"name\": \"" + assemblyName + "\""),
                "The asmdef at " + path + " does not declare the assembly name " + assemblyName + ".");

            var references = new List<string>();
            var index = text.IndexOf("\"references\"", StringComparison.Ordinal);
            Assert.GreaterOrEqual(index, 0, "The asmdef at " + path + " declares no references array.");

            var open = text.IndexOf('[', index);
            var close = text.IndexOf(']', open);
            Assert.Greater(open, index);
            Assert.Greater(close, open);

            var body = text.Substring(open + 1, close - open - 1);
            foreach (var raw in body.Split(','))
            {
                var value = raw.Trim().Trim('"');
                if (value.Length > 0) references.Add(value);
            }

            return references.ToArray();
        }

        /// <summary>
        /// The package root, resolved from <see cref="Application.dataPath"/> the same way the other source
        /// contract tests resolve it, so the test never depends on the process working directory.
        /// </summary>
        private static string PackageRoot()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var root = Path.Combine(projectRoot, "Packages", "dev.avatar-part-assembler");
            Assert.IsTrue(Directory.Exists(root), "The package root does not exist: " + root);
            return root;
        }
    }
}
