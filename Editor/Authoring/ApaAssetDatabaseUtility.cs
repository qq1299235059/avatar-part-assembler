using System;
using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The small set of asset-database operations the authoring writers share.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="ApaAuthoringAssetPaths"/>, which is deliberately pure and testable without
    /// an asset database. Everything that actually touches the database lives here, so that "does this path
    /// make sense" and "does this path exist" remain two different questions with two different answers.
    /// </remarks>
    public static class ApaAssetDatabaseUtility
    {
        /// <summary>
        /// Creates every missing folder above an asset path, so a write cannot fail because the author typed a
        /// new folder name. An asset path directly under <c>Assets/</c> needs no folder.
        /// </summary>
        public static void EnsureFolder(string assetPath)
        {
            var folders = ApaAuthoringAssetPaths.FolderSegments(assetPath);
            if (folders.Length <= 1) return;

            var current = folders[0];
            for (var i = 1; i < folders.Length; i++)
            {
                var next = current + "/" + folders[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, folders[i]);
                }

                current = next;
            }
        }

        /// <summary>Loads an asset at a path, or returns null.</summary>
        public static T Load<T>(string assetPath) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            return AssetDatabase.LoadAssetAtPath<T>(assetPath);
        }

        /// <summary>
        /// The main asset at a path, whatever its type, or null.
        /// </summary>
        /// <remarks>
        /// <see cref="Load{T}"/> answers "is there an asset of this type at the path", which is the wrong
        /// question for a write decision: a typed load returns null for a material, a texture, or a prefab, so a
        /// caller that treats null as "nothing is there" would hand the path to <c>AssetDatabase.CreateAsset</c>,
        /// which deletes whatever occupies it. This method answers "is the path in use at all", so a write can
        /// refuse a path occupied by a different asset type instead of destroying it.
        /// </remarks>
        public static UnityEngine.Object LoadMainAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            return AssetDatabase.LoadMainAssetAtPath(assetPath);
        }

        /// <summary>
        /// True when the object is a persisted project asset rather than a scene object or a transient instance.
        /// </summary>
        /// <remarks>
        /// This is the single predicate behind every "can this reference be stored in a reusable asset" decision
        /// in the authoring layer. A scene object answers false, and so does a transient instance created at
        /// runtime, which is exactly the set of references a profile or a prefab must never contain.
        /// </remarks>
        public static bool IsPersistent(UnityEngine.Object value)
        {
            return value != null && EditorUtility.IsPersistent(value);
        }

        /// <summary>
        /// True when a reference names the asset at a path, as the asset database resolves that path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the half of an asset-identity check that survives a reload. A caller that wrote an asset and
        /// then read a file referencing it cannot rely on the wrapper it wrote with: a save, an import, or a
        /// prefab load can hand out a second wrapper for the same asset, or leave the first one detached. The
        /// asset database path is what a recipient resolves the reference to, so "the reference is this asset" is
        /// a question about the path before it is a question about the instance.
        /// </para>
        /// <para>
        /// A scene object, a transient instance, an object inside another asset, and an asset in a different file
        /// all answer false. The predicate is used to prove that a written prefab carries the payload a creation
        /// wrote, so a near miss must fail the proof rather than pass it.
        /// </para>
        /// </remarks>
        public static bool IsAssetAtPath(UnityEngine.Object asset, string assetPath)
        {
            if (asset == null || string.IsNullOrEmpty(assetPath)) return false;
            if (!IsPersistent(asset)) return false;

            var path = AssetDatabase.GetAssetPath(asset);
            return !string.IsNullOrEmpty(path) && ApaAuthoringAssetPaths.PathsEqual(path, assetPath);
        }

        /// <summary>
        /// True when two references name the same Unity asset, comparing native object identity rather than the
        /// managed wrapper.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="object.ReferenceEquals"/> answers a different question than "is this the same asset". Unity
        /// can hold two managed wrappers for one native object — a prefab that was saved and loaded back, an asset
        /// database reload, a reference deserialized from a prefab file — and those wrappers are not
        /// reference-equal even though both name the same asset. The <c>==</c> operator is Unity's own identity
        /// test: it compares the native object, so two wrappers of one asset are equal and two assets never are.
        /// </para>
        /// <para>
        /// The GUID and local file identifier are the fallback for the case where even the native comparison
        /// cannot see the equality. They identify a persisted asset exactly — file plus sub-asset — so a
        /// genuinely different asset still answers false, which is the point of the check: a mismatch must fail
        /// closed rather than be tolerated as "close enough".
        /// </para>
        /// </remarks>
        public static bool SameAsset(UnityEngine.Object first, UnityEngine.Object second)
        {
            // Unity's == is the identity test this predicate is named for: it is true for the same managed
            // instance and for two wrappers of one native object, and false for two assets.
            if (first == null || second == null) return false;
            if (first == second) return true;

            // Neither wrapper resolves to a persisted asset, so there is no asset identity to compare: two scene
            // objects, two transient instances, or one of each can only be reported as different.
            if (!IsPersistent(first) || !IsPersistent(second)) return false;

            var firstPath = AssetDatabase.GetAssetPath(first);
            var secondPath = AssetDatabase.GetAssetPath(second);
            if (string.IsNullOrEmpty(firstPath) || string.IsNullOrEmpty(secondPath)) return false;
            if (!ApaAuthoringAssetPaths.PathsEqual(firstPath, secondPath)) return false;

            // The identifier is explicitly typed: the asset database offers both an int and a long overload, and
            // an implicitly typed argument makes the call ambiguous.
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(first, out var firstGuid, out long firstLocalId)
                   && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(second, out var secondGuid, out long secondLocalId)
                   && string.Equals(firstGuid, secondGuid, StringComparison.Ordinal)
                   && firstLocalId == secondLocalId;
        }

        /// <summary>
        /// True when a reference is the asset at a path, and — while the written instance is still available — is
        /// also that same Unity object.
        /// </summary>
        /// <param name="reference">The reference read back from the file that was written.</param>
        /// <param name="assetPath">The path the caller wrote the asset to.</param>
        /// <param name="written">
        /// The instance the write returned, or null when it is no longer available — an asset database reload can
        /// detach it, and that is not evidence against the reference.
        /// </param>
        /// <remarks>
        /// <para>
        /// This is the whole "is this the asset I wrote" rule in one place, so a caller that has to prove it does
        /// not have to re-derive which half is authoritative. The path is required, because that is what a
        /// recipient resolves the reference to and what survives a reload; the written instance is required too
        /// whenever it can still be compared, because a reference that resolves to the right path but is not the
        /// object that was written is a state this package never produces and must not publish.
        /// </para>
        /// <para>
        /// Both halves fail closed: a reference to another asset, to a transient instance, or to a scene object
        /// answers false whatever the written instance is.
        /// </para>
        /// </remarks>
        public static bool IsSameAssetAtPath(
            UnityEngine.Object reference,
            string assetPath,
            UnityEngine.Object written)
        {
            if (!IsAssetAtPath(reference, assetPath)) return false;
            return written == null || SameAsset(reference, written);
        }

        /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or inside it.</summary>
        public static bool IsInsideSubtree(Transform candidate, Transform root)
        {
            if (candidate == null || root == null) return false;

            var current = candidate;
            while (current != null)
            {
                if (current == root) return true;
                current = current.parent;
            }

            return false;
        }

        /// <summary>
        /// Deletes an asset that this session just created, as the rollback half of a create operation.
        /// </summary>
        /// <remarks>
        /// Used only for an asset created moments earlier in the same call: deleting an asset the author already
        /// had would destroy work, so the caller must know that the file did not exist before.
        /// </remarks>
        public static void RollBackCreatedAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null) return;

            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.Refresh();
        }
    }
}
