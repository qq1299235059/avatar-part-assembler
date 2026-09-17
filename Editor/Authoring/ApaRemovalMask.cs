using System;
using System.Collections.Generic;
using UnityEngine;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The editable body-removal triangle set of one part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the authoring-side editor state for <see cref="ApaRemovalProfile"/>. It is a separate type, and
    /// not the profile itself, for three reasons:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The profile is a serialized asset whose setter replaces the whole set. Interactive editing needs
    /// incremental add, remove, and toggle, and it needs to report a duplicate instead of silently collapsing
    /// it.
    /// </description></item>
    /// <item><description>
    /// The mask has to survive Unity's undo and domain-reload serialization as a plain <c>[Serializable]</c>
    /// value, so it stores two parallel arrays — the same serialized shape as the profile — rather than a
    /// hash set, which Unity cannot serialize.
    /// </description></item>
    /// <item><description>
    /// It carries the non-authoritative structural check that gives the picker live feedback before a
    /// validation context can exist. The authoritative check remains <see cref="RemovalRule"/>, reached
    /// through <c>ApaCore.Validate</c>; this class deliberately reports the same codes and detail tokens as that
    /// rule so that the two cannot tell the author two different stories.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Invariant.</b> The two arrays always have the same length, are sorted ascending by
    /// (<c>submesh</c>, <c>triangle</c>), and contain no duplicates. <see cref="HasCorruptStorage"/> exists
    /// because a value restored from a damaged serialized form could violate the invariant, and a mismatched
    /// pair cannot be repaired without guessing which entry is missing.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ApaRemovalMask
    {
        [SerializeField] private int[] _subMeshIndices = Array.Empty<int>();
        [SerializeField] private int[] _triangleIndices = Array.Empty<int>();

        /// <summary>Number of removed triangles.</summary>
        public int Count => _subMeshIndices != null ? _subMeshIndices.Length : 0;

        /// <summary>True when nothing is removed.</summary>
        public bool IsEmpty => Count == 0;

        /// <summary>
        /// True when the serialized arrays disagree in length. Always a hard failure, never repaired.
        /// </summary>
        public bool HasCorruptStorage
        {
            get
            {
                var subMeshes = _subMeshIndices ?? Array.Empty<int>();
                var triangles = _triangleIndices ?? Array.Empty<int>();
                return subMeshes.Length != triangles.Length;
            }
        }

        /// <summary>The address at a canonical index.</summary>
        public RemovedTriangleAddress GetAddress(int canonicalIndex)
        {
            if (canonicalIndex < 0 || canonicalIndex >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(canonicalIndex));
            }

            return new RemovedTriangleAddress(_subMeshIndices[canonicalIndex], _triangleIndices[canonicalIndex]);
        }

        /// <summary>Every address, in canonical order.</summary>
        public RemovedTriangleAddress[] ToAddresses()
        {
            if (HasCorruptStorage) return Array.Empty<RemovedTriangleAddress>();

            var result = new RemovedTriangleAddress[Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new RemovedTriangleAddress(_subMeshIndices[i], _triangleIndices[i]);
            }

            return result;
        }

        /// <summary>Binary search over the canonical order.</summary>
        public bool Contains(RemovedTriangleAddress address)
        {
            if (HasCorruptStorage) return false;

            var low = 0;
            var high = Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var candidate = new RemovedTriangleAddress(_subMeshIndices[middle], _triangleIndices[middle]);
                var comparison = candidate.CompareTo(address);
                if (comparison == 0) return true;
                if (comparison < 0) low = middle + 1;
                else high = middle - 1;
            }

            return false;
        }

        /// <summary>
        /// Adds one address, keeping the arrays sorted and duplicate-free.
        /// </summary>
        /// <returns>True when the address was added; false when it was already present.</returns>
        public bool Add(RemovedTriangleAddress address)
        {
            EnsureArrays();

            var index = LowerBound(address);
            if (index < Count && _subMeshIndices[index] == address.SubMeshIndex
                              && _triangleIndices[index] == address.TriangleIndexWithinSubMesh)
            {
                return false;
            }

            InsertAt(index, address);
            return true;
        }

        /// <summary>
        /// Adds every address in the sequence, reporting any that were already present or repeated inside the
        /// input rather than silently dropping them.
        /// </summary>
        /// <param name="duplicates">
        /// Receives each address that was not added because it was already in the set or earlier in the input.
        /// Never null when supplied by the pipeline; a null list discards the report.
        /// </param>
        /// <returns>The number of addresses actually added.</returns>
        public int AddRange(IEnumerable<RemovedTriangleAddress> addresses, List<RemovedTriangleAddress> duplicates)
        {
            if (addresses == null) return 0;

            var added = 0;
            foreach (var address in addresses)
            {
                if (Add(address)) added++;
                else duplicates?.Add(address);
            }

            return added;
        }

        /// <summary>Removes one address.</summary>
        /// <returns>True when the address was present and removed.</returns>
        public bool Remove(RemovedTriangleAddress address)
        {
            if (HasCorruptStorage) return false;

            var index = LowerBound(address);
            if (index >= Count) return false;
            if (_subMeshIndices[index] != address.SubMeshIndex
                || _triangleIndices[index] != address.TriangleIndexWithinSubMesh)
            {
                return false;
            }

            RemoveAt(index);
            return true;
        }

        /// <summary>Adds the address when absent, removes it when present.</summary>
        /// <returns>True when the address is present after the call.</returns>
        public bool Toggle(RemovedTriangleAddress address)
        {
            if (Remove(address)) return false;
            Add(address);
            return true;
        }

        /// <summary>Removes every address belonging to one submesh.</summary>
        /// <returns>The number of addresses removed.</returns>
        public int RemoveSubMesh(int subMeshIndex)
        {
            if (HasCorruptStorage) return 0;

            var removed = 0;
            for (var i = Count - 1; i >= 0; i--)
            {
                if (_subMeshIndices[i] != subMeshIndex) continue;
                RemoveAt(i);
                removed++;
            }

            return removed;
        }

        /// <summary>Removes everything.</summary>
        public void Clear()
        {
            _subMeshIndices = Array.Empty<int>();
            _triangleIndices = Array.Empty<int>();
        }

        /// <summary>Replaces the whole set, canonicalizing the input.</summary>
        public void SetFromAddresses(IEnumerable<RemovedTriangleAddress> addresses)
        {
            Clear();
            if (addresses == null) return;

            var list = new List<RemovedTriangleAddress>();
            foreach (var address in addresses) list.Add(address);
            list.Sort();

            var count = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0 && list[i].Equals(list[i - 1])) continue;
                list[count++] = list[i];
            }

            if (count != list.Count) list.RemoveRange(count, list.Count - count);

            _subMeshIndices = new int[list.Count];
            _triangleIndices = new int[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                _subMeshIndices[i] = list[i].SubMeshIndex;
                _triangleIndices[i] = list[i].TriangleIndexWithinSubMesh;
            }
        }

        /// <summary>An independent copy, used by undo-friendly edit actions and by tests.</summary>
        public ApaRemovalMask Clone()
        {
            return new ApaRemovalMask
            {
                _subMeshIndices = (int[])(_subMeshIndices ?? Array.Empty<int>()).Clone(),
                _triangleIndices = (int[])(_triangleIndices ?? Array.Empty<int>()).Clone()
            };
        }

        /// <summary>Builds the serialized profile form of this mask.</summary>
        public ApaRemovalProfile ToRemovalProfile()
        {
            var profile = new ApaRemovalProfile();
            profile.RemovedTriangles = new RemovedTriangleAddressSet(ToAddresses());
            return profile;
        }

        /// <summary>Reads the mask out of a serialized profile.</summary>
        public static ApaRemovalMask FromRemovalProfile(ApaRemovalProfile profile)
        {
            var mask = new ApaRemovalMask();
            if (profile == null) return mask;
            mask.SetFromAddresses(profile.RemovedTriangles.Addresses);
            return mask;
        }

        /// <summary>
        /// Non-authoritative structural check for live editor feedback.
        /// </summary>
        /// <param name="triangleCountsPerSubMesh">
        /// Triangle count of each submesh of the target mesh, or null when no target mesh is selected yet. A
        /// null list means "cannot check", not "check passed", so no in-range issue is invented.
        /// </param>
        /// <param name="topologies">Topology of each submesh, or null when unknown.</param>
        /// <param name="partId">Part identity used in the diagnostics.</param>
        /// <remarks>
        /// A duplicate inside the return value cannot happen: the mask de-duplicates on insertion and reports
        /// the duplicate to the caller at that moment. This method checks what the canonical set cannot say by
        /// itself — whether the addresses still name triangles of the selected target mesh.
        /// </remarks>
        public List<ValidationIssue> Validate(
            IReadOnlyList<int> triangleCountsPerSubMesh,
            IReadOnlyList<MeshTopology> topologies,
            string partId)
        {
            var issues = new List<ValidationIssue>();
            if (HasCorruptStorage)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InvalidTriangleAddress,
                    ApaIssuePhase.Removal,
                    "The removal mask has mismatched submesh and triangle arrays. The missing component cannot " +
                    "be reconstructed without guessing; re-author the removal selection.",
                    partId,
                    detail: "reason=corrupt-removal-storage"));
                return issues;
            }

            if (triangleCountsPerSubMesh == null) return issues;

            for (var i = 0; i < Count; i++)
            {
                var subMesh = _subMeshIndices[i];
                var triangle = _triangleIndices[i];
                var address = new RemovedTriangleAddress(subMesh, triangle);

                if (subMesh < 0 || triangle < 0)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidTriangleAddress,
                        ApaIssuePhase.Removal,
                        "A removal address has a negative component, so it cannot name a triangle. Submesh " +
                        subMesh + ", triangle " + triangle + ".",
                        partId,
                        triangle,
                        subMesh,
                        detail: address + "; reason=negative-component"));
                    continue;
                }

                if (subMesh >= triangleCountsPerSubMesh.Count)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidTriangleAddress,
                        ApaIssuePhase.Removal,
                        "A removal address names submesh " + subMesh + ", but the target mesh has " +
                        triangleCountsPerSubMesh.Count + " submesh(es). The profile was probably authored " +
                        "against a different body.",
                        partId,
                        triangle,
                        subMesh,
                        detail: address + "; subMeshCount=" + triangleCountsPerSubMesh.Count +
                                "; reason=submesh-out-of-range"));
                    continue;
                }

                if (topologies != null && subMesh < topologies.Count
                                       && topologies[subMesh] != MeshTopology.Triangles)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidTriangleAddress,
                        ApaIssuePhase.Removal,
                        "A removal address names submesh " + subMesh + ", whose topology is " + topologies[subMesh] +
                        ". Only triangle submeshes can be addressed.",
                        partId,
                        triangle,
                        subMesh,
                        detail: address + "; topology=" + topologies[subMesh] + "; reason=not-a-triangle-list"));
                    continue;
                }

                var triangleCount = triangleCountsPerSubMesh[subMesh];
                if (triangle >= triangleCount)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.RemovalIndexOutOfRange,
                        ApaIssuePhase.Removal,
                        "Removal triangle " + triangle + " is outside submesh " + subMesh + ", which has " +
                        triangleCount + " triangle(s).",
                        partId,
                        triangle,
                        subMesh,
                        detail: address + "; triangleCount=" + triangleCount));
                }
            }

            return issues;
        }

        /// <summary>
        /// Parses the numeric fallback the picker cannot replace: a list of triangle addresses written as
        /// <c>submesh:triangle</c>, with an optional inclusive triangle range such as <c>0:4-9</c>.
        /// </summary>
        /// <param name="text">Author input. Commas, semicolons, and any whitespace separate entries.</param>
        /// <param name="addresses">
        /// On success, the parsed addresses in input order. Duplicates are preserved so the caller can report
        /// them rather than silently collapsing author input.
        /// </param>
        /// <param name="error">
        /// On failure, a stable token: <c>malformed:&lt;token&gt;</c>, <c>invalid-range:&lt;token&gt;</c>, or
        /// <c>too-large:&lt;token&gt;</c>.
        /// </param>
        /// <returns>True when the text parsed. Empty input succeeds and yields an empty list.</returns>
        public static bool TryParseAddressList(string text, out RemovedTriangleAddress[] addresses, out string error)
        {
            addresses = Array.Empty<RemovedTriangleAddress>();
            error = string.Empty;

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return true;

            var tokens = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<RemovedTriangleAddress>(tokens.Length);

            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i].Trim();
                if (token.Length == 0) continue;

                var separator = token.IndexOf(':');
                if (separator <= 0 || separator == token.Length - 1)
                {
                    error = "malformed:" + token;
                    addresses = Array.Empty<RemovedTriangleAddress>();
                    return false;
                }

                if (!int.TryParse(
                        token.Substring(0, separator),
                        System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var subMesh))
                {
                    error = "malformed:" + token;
                    addresses = Array.Empty<RemovedTriangleAddress>();
                    return false;
                }

                var trianglePart = token.Substring(separator + 1);
                var rangeSeparator = trianglePart.IndexOf('-', 1);

                if (rangeSeparator < 0)
                {
                    if (!int.TryParse(
                            trianglePart,
                            System.Globalization.NumberStyles.AllowLeadingSign,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var triangle))
                    {
                        error = "malformed:" + token;
                        addresses = Array.Empty<RemovedTriangleAddress>();
                        return false;
                    }

                    result.Add(new RemovedTriangleAddress(subMesh, triangle));
                    continue;
                }

                var startText = trianglePart.Substring(0, rangeSeparator);
                var endText = trianglePart.Substring(rangeSeparator + 1);

                if (!int.TryParse(startText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var start)
                    || !int.TryParse(endText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var end)
                    || end < start)
                {
                    error = "invalid-range:" + token;
                    addresses = Array.Empty<RemovedTriangleAddress>();
                    return false;
                }

                if ((long)end - start > 100000L)
                {
                    error = "too-large:" + token;
                    addresses = Array.Empty<RemovedTriangleAddress>();
                    return false;
                }

                for (var triangle = start; triangle <= end; triangle++)
                {
                    result.Add(new RemovedTriangleAddress(subMesh, triangle));
                }
            }

            addresses = result.ToArray();
            return true;
        }

        /// <summary>
        /// A short status line: how many triangles are removed in how many submeshes.
        /// </summary>
        public string Describe()
        {
            if (HasCorruptStorage) return Tr("corrupt storage (mismatched arrays)");
            if (Count == 0) return Tr("no triangles removed");

            var subMeshes = 0;
            for (var i = 0; i < Count; i++)
            {
                if (i == 0 || _subMeshIndices[i] != _subMeshIndices[i - 1]) subMeshes++;
            }

            return TrFormat("{0} triangle(s) in {1} submesh(es)", Count, subMeshes);
        }

        /// <summary>
        /// Index of the first canonical entry that is not less than the address. Insertion at this index keeps
        /// the array sorted in O(n) without re-sorting on every picked triangle.
        /// </summary>
        private int LowerBound(RemovedTriangleAddress address)
        {
            var low = 0;
            var high = Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                var candidate = new RemovedTriangleAddress(_subMeshIndices[middle], _triangleIndices[middle]);
                if (candidate.CompareTo(address) < 0) low = middle + 1;
                else high = middle;
            }

            return low;
        }

        private void InsertAt(int index, RemovedTriangleAddress address)
        {
            var subMeshes = new int[Count + 1];
            var triangles = new int[Count + 1];
            for (var i = 0; i < index; i++)
            {
                subMeshes[i] = _subMeshIndices[i];
                triangles[i] = _triangleIndices[i];
            }

            subMeshes[index] = address.SubMeshIndex;
            triangles[index] = address.TriangleIndexWithinSubMesh;

            for (var i = index; i < Count; i++)
            {
                subMeshes[i + 1] = _subMeshIndices[i];
                triangles[i + 1] = _triangleIndices[i];
            }

            _subMeshIndices = subMeshes;
            _triangleIndices = triangles;
        }

        private void RemoveAt(int index)
        {
            if (index < 0 || index >= Count) return;

            var subMeshes = new int[Count - 1];
            var triangles = new int[Count - 1];
            for (var i = 0; i < index; i++)
            {
                subMeshes[i] = _subMeshIndices[i];
                triangles[i] = _triangleIndices[i];
            }

            for (var i = index + 1; i < Count; i++)
            {
                subMeshes[i - 1] = _subMeshIndices[i];
                triangles[i - 1] = _triangleIndices[i];
            }

            _subMeshIndices = subMeshes;
            _triangleIndices = triangles;
        }

        private void EnsureArrays()
        {
            if (_subMeshIndices == null) _subMeshIndices = Array.Empty<int>();
            if (_triangleIndices == null) _triangleIndices = Array.Empty<int>();

            // A damaged pair cannot be repaired, so it is replaced by an empty set only after the caller has had
            // the chance to see HasCorruptStorage. Editing a corrupt mask is not a supported operation.
            if (_subMeshIndices.Length != _triangleIndices.Length) Clear();
        }
    }
}
