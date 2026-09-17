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
