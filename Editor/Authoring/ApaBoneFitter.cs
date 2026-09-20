using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using nadena.dev.modular_avatar.core;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// Aligns a part's skeleton onto the avatar's current pose, bone pair by bone pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Modular Avatar's merge reparents each part bone under its same-named avatar bone
    /// with <c>worldPositionStays: true</c> — the merge preserves the part bone's <i>world</i> pose and changes
    /// only the parent. A part authored against a rest-pose body therefore keeps that rest pose after the merge,
    /// even when the avatar's bones have since been moved or scaled: the merged outfit does not follow the posed
    /// avatar. The fix is not in the merge; it is in the authoring hierarchy. When the part's bones carry the
    /// avatar's current world pose before the merge, the merge becomes a no-op on pose and the part rides the
    /// avatar's skeleton exactly.
    /// </para>
    /// <para>
    /// <b>Matching mirrors the merge.</b> The bone pairing below replicates Modular Avatar's zip merge for the
    /// configuration this package generates: children of the part armature are matched against children of the
    /// target armature by exact name (the generated configuration always sets an empty prefix and suffix), and
    /// matched pairs recurse. A subtree that carries its own <see cref="ModularAvatarMergeArmature"/> is skipped,
    /// exactly as the merge itself skips it. What this fitter matches is therefore what the merge will merge —
    /// there is no second mapping rule to drift out of sync with the first.
    /// </para>
    /// <para>
    /// <b>The write is world-space and one-sided.</b> Each part bone's world position and rotation are set to its
    /// avatar counterpart's; the avatar side is never written, so every read in a pass is stable regardless of
    /// order. Scale is optional: copying it matters when the avatar's bones have been rescaled, because a part
    /// bone that keeps the old scale drags its subtree away from the merged result.
    /// </para>
    /// </remarks>
    public static class ApaBoneFitter
    {
        /// <summary>A matched part bone and the avatar bone it will follow.</summary>
        public readonly struct BonePair
        {
            /// <summary>The part-side bone that receives the pose.</summary>
            public readonly Transform Part;

            /// <summary>The avatar-side bone whose pose is copied.</summary>
            public readonly Transform Avatar;

            /// <summary>Creates a pair.</summary>
            public BonePair(Transform part, Transform avatar)
            {
                Part = part;
                Avatar = avatar;
            }
        }

        /// <summary>
        /// Resolves the two armatures a part's skeleton is defined against, from the profile's own selections.
        /// </summary>
        /// <remarks>
        /// The same resolution the preview bone map and the merge planner perform: the part armature is resolved
        /// under the part root and the target armature under the avatar root, each from the path its profile
        /// recorded. Nothing is derived from bone names here — a selection that does not resolve is reported as a
        /// reason the caller can show, not guessed around.
        /// </remarks>
        /// <param name="installer">The installer whose profile carries the two selections.</param>
        /// <param name="partArmature">The resolved part armature, or null.</param>
        /// <param name="targetArmature">The resolved target armature, or null.</param>
        /// <param name="reason">
        /// When false, a stable reason token: <c>no-part-root</c>, <c>no-avatar-root</c>, <c>no-selection</c>,
        /// <c>resolve-part</c>, or <c>resolve-target</c>. The caller translates it for display.
        /// </param>
        /// <returns>True when both armatures resolved.</returns>
        public static bool TryResolveArmatures(
            AvatarPartInstaller installer,
            out Transform partArmature,
            out Transform targetArmature,
            out string reason)
        {
            partArmature = null;
            targetArmature = null;
            reason = null;

            if (installer == null)
            {
                reason = "no-installer";
                return false;
            }

            var partRoot = installer.ResolvePartRoot();
            if (partRoot == null)
            {
                reason = "no-part-root";
                return false;
            }

            var avatarAnchor = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
            if (avatarAnchor == null)
            {
                reason = "no-avatar-root";
                return false;
            }

            var bones = installer.Profile != null ? installer.Profile.BonesOrNull : null;
            var partPath = bones != null ? bones.PartArmaturePath : string.Empty;
            var targetPath = bones != null ? bones.TargetArmaturePath : string.Empty;
            if (!ApaAvatarPath.HasIdentity(partPath) || !ApaAvatarPath.HasIdentity(targetPath))
            {
                reason = "no-selection";
                return false;
            }

            var partId = ApaPartIdentityResolver.ResolvePartId(installer);
            var issues = new List<ValidationIssue>();

            if (!ApaArmatureScope.TryResolve(
                    partRoot.transform, partPath, "part", partId, "the part root", issues, out var part))
            {
                reason = "resolve-part";
                return false;
            }

            if (!ApaArmatureScope.TryResolve(
                    avatarAnchor.gameObject.transform, targetPath, "target", partId, "the avatar root",
                    issues, out var target))
            {
                reason = "resolve-target";
                return false;
            }

            partArmature = part;
            targetArmature = target;
            return true;
        }

        /// <summary>
        /// Collects the part-to-avatar bone pairs the merge will merge, by exact-name walk from the two armatures.
        /// </summary>
        /// <param name="partArmature">The part armature the walk starts from.</param>
        /// <param name="targetArmature">The target armature whose children the part's children are matched against.</param>
        /// <param name="pairs">Receives one pair per matched bone, parents before children.</param>
        /// <param name="unmatched">
        /// Receives the names of part-side children that found no counterpart, in walk order: a bone with no
        /// same-named avatar bone stays where it is, and so does a subtree that carries its own merge
        /// configuration (the merge skips those too).
        /// </param>
        public static void CollectBonePairs(
            Transform partArmature,
            Transform targetArmature,
            List<BonePair> pairs,
            List<string> unmatched)
        {
            if (partArmature == null || targetArmature == null) return;
            if (pairs == null || unmatched == null) return;

            Walk(partArmature, targetArmature, pairs, unmatched);
        }

        /// <summary>Writes the avatar bones' world pose onto the matching part bones. The avatar side is untouched.</summary>
        /// <remarks>
        /// The caller records undo for the part transforms before calling this; the write itself is plain transform
        /// assignment so that the live-follow loop can share it without wrapping every frame in an undo group.
        /// </remarks>
        public static void ApplyPose(IReadOnlyList<BonePair> pairs, bool includeScale)
        {
            if (pairs == null) return;

            for (var i = 0; i < pairs.Count; i++)
            {
                ApplyOne(pairs[i], includeScale);
            }
        }

        /// <summary>Writes one pair's pose. The shared core of the one-shot sync and the live-follow loop;
        /// internal so the follow loop can call it without allocating a one-element array per bone.</summary>
        internal static void ApplyOne(BonePair pair, bool includeScale)
        {
            var part = pair.Part;
            var avatar = pair.Avatar;
            if (part == null || avatar == null) return;

            part.position = avatar.position;
            part.rotation = avatar.rotation;
            if (includeScale) part.localScale = avatar.localScale;
        }

        /// <summary>
        /// The recursive half of <see cref="CollectBonePairs"/>, shaped after Modular Avatar's zip merge walk.
        /// </summary>
        private static void Walk(
            Transform partBone,
            Transform targetBone,
            List<BonePair> pairs,
            List<string> unmatched)
        {
            // A snapshot of the child list is not needed on the part side — the walk never reparents anything,
            // only reads — so iterating the live children is safe here, unlike in the merge itself.
            for (var i = 0; i < partBone.childCount; i++)
            {
                var child = partBone.GetChild(i);

                // A subtree with its own merge configuration is merged by that configuration, not by the walk
                // above it; matching it here would pair bones the merge will not touch.
                if (child.GetComponent<ModularAvatarMergeArmature>() != null)
                {
                    unmatched.Add(child.name);
                    continue;
                }

                var counterpart = targetBone.Find(child.name);
                if (counterpart == null)
                {
                    unmatched.Add(child.name);
                    continue;
                }

                pairs.Add(new BonePair(child, counterpart));
                Walk(child, counterpart, pairs, unmatched);
            }
        }
    }

    /// <summary>
    /// The live half of the bone fit: while enabled for an installer, its matched part bones follow the avatar's
    /// bones on every editor update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Editor-only, session-local, opt-in.</b> The state lives in a static table keyed by instance id, so it
    /// survives inspector repaints and closing the inspector — posing happens in the Scene View, not in the
    /// inspector — and resets on a domain reload, which is the safe direction for a mode that writes transforms
    /// continuously. Nothing here touches the build: the runtime component carries no field for it, and the loop
    /// idles entirely in play mode, where the avatar's pose belongs to animation rather than to the author.
    /// </para>
    /// <para>
    /// <b>The writes are not undoable</b>, and that is stated in the inspector rather than papered over: an undo
    /// per frame would flood the undo stack and make the feature unusable. Turning the toggle off stops the loop
    /// and leaves the bones wherever they are, which is exactly the "pose now, keep it" workflow.
    /// </para>
    /// <para>
    /// The pair list is built once when following starts and is then only cheaply re-validated per frame: a
    /// destroyed bone on either side skips its pair, and a session whose installer or every pair has died removes
    /// itself. Re-selecting armatures or replacing the part requires toggling off and on, which the inspector
    /// states next to the toggle.
    /// </para>
    /// </remarks>
    internal static class ApaBoneFollowRuntime
    {
        private sealed class Session
        {
            internal AvatarPartInstaller Installer;
            internal List<ApaBoneFitter.BonePair> Pairs;
            internal bool IncludeScale;
        }

        private static readonly List<Session> s_sessions = new List<Session>();
        private static bool s_updating;

        /// <summary>True when the installer is currently following the avatar's bones.</summary>
        public static bool IsFollowing(AvatarPartInstaller installer)
        {
            if (installer == null) return false;
            for (var i = 0; i < s_sessions.Count; i++)
            {
                if (ReferenceEquals(s_sessions[i].Installer, installer)) return true;
            }

            return false;
        }

        /// <summary>The scale option the session was started or last updated with.</summary>
        public static bool IsIncludingScale(AvatarPartInstaller installer)
        {
            for (var i = 0; i < s_sessions.Count; i++)
            {
                if (ReferenceEquals(s_sessions[i].Installer, installer)) return s_sessions[i].IncludeScale;
            }

            return false;
        }

        /// <summary>
        /// Turns following on or off, or refreshes an active session's pair list and scale option.
        /// </summary>
        /// <remarks>
        /// Turning on (or refreshing) rebuilds the pair list from the installer's current armature selections, so
        /// an author who changed the selections or the part can refresh without a domain reload.
        /// </remarks>
        public static void SetFollowing(AvatarPartInstaller installer, bool following, bool includeScale)
        {
            if (installer == null) return;

            var index = IndexOf(installer);
            if (following)
            {
                if (!ApaBoneFitter.TryResolveArmatures(installer, out var partArmature, out var targetArmature, out _))
                {
                    if (index >= 0) RemoveAt(index);
                    return;
                }

                var pairs = new List<ApaBoneFitter.BonePair>();
                ApaBoneFitter.CollectBonePairs(partArmature, targetArmature, pairs, new List<string>());

                if (index >= 0)
                {
                    s_sessions[index].Pairs = pairs;
                    s_sessions[index].IncludeScale = includeScale;
                }
                else
                {
                    s_sessions.Add(new Session
                    {
                        Installer = installer,
                        Pairs = pairs,
                        IncludeScale = includeScale
                    });
                }

                Subscribe();
            }
            else if (index >= 0)
            {
                RemoveAt(index);
            }
        }

        private static int IndexOf(AvatarPartInstaller installer)
        {
            for (var i = 0; i < s_sessions.Count; i++)
            {
                if (ReferenceEquals(s_sessions[i].Installer, installer)) return i;
            }

            return -1;
        }

        private static void RemoveAt(int index)
        {
            s_sessions.RemoveAt(index);
            if (s_sessions.Count == 0) Unsubscribe();
        }

        private static void Subscribe()
        {
            if (s_updating) return;
            EditorApplication.update += Update;
            s_updating = true;
        }

        private static void Unsubscribe()
        {
            if (!s_updating) return;
            EditorApplication.update -= Update;
            s_updating = false;
        }

        private static void Update()
        {
            // In play mode the avatar's pose belongs to animation and the authoring hierarchy is not what the
            // user is looking at; a loop writing bones there would fight the animator.
            if (Application.isPlaying) return;

            for (var i = s_sessions.Count - 1; i >= 0; i--)
            {
                var session = s_sessions[i];

                // A destroyed installer (scene closed, object deleted) ends its session.
                if (session.Installer == null)
                {
                    RemoveAt(i);
                    continue;
                }

                var alive = 0;
                for (var p = 0; p < session.Pairs.Count; p++)
                {
                    var pair = session.Pairs[p];
                    if (pair.Part == null || pair.Avatar == null) continue;

                    // Per-frame path: the shared single-pair core, not ApplyPose, which would allocate an
                    // array per bone per update.
                    ApaBoneFitter.ApplyOne(pair, session.IncludeScale);
                    alive++;
                }

                if (alive == 0) RemoveAt(i);
            }
        }
    }
}
