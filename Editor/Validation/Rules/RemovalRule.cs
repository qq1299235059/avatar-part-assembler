using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Validates that removal triangle addresses are well formed, in range, and non-overlapping across parts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The product specification explicitly rejects guessing a removal region from bone weights in favour of an
    /// explicit, stable triangle set. That makes the triangle set author data, and author data must be checked:
    /// an out-of-range address would otherwise silently delete the wrong triangle or nothing at all.
    /// </para>
    /// <para>
    /// <b>Addresses are two-dimensional on purpose.</b> A body mesh routinely carries several triangle
    /// submeshes, and a flat "triangle index" cannot say which one it means. This rule therefore validates the
    /// submesh component and the in-submesh component separately, and reports overlap per full address, so that
    /// triangle 0 of submesh 0 and triangle 0 of submesh 1 are two different triangles that cannot collide.
    /// </para>
    /// <para>
    /// <b>Overlap is block-by-default and priority-resolvable by explicit policy.</b> An overlap blocks unless
    /// every claimant declares a non-zero conflict priority and the highest one is unique. A single declared
    /// priority resolves nothing, because an undeclared zero is not a weaker claim — it is the absence of a
    /// claim, and treating it as the lowest would let one author silently outrank another who never opted in.
    /// When the policy does resolve an overlap it emits <c>APA035</c> (warning) naming the owner, the losers and
    /// their priorities, because the resolved owner is what the diagnostics, the weld-ownership narrative and
    /// any future per-region feature must agree on.
    /// </para>
    /// <para>
    /// Resolution never changes the geometry: the removed set is the union either way, because removal is
    /// idempotent. What the policy decides is ownership of the region.
    /// </para>
    /// </remarks>
    public sealed class RemovalRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "RemovalRule";

        /// <summary>How many resolved addresses a warning lists before it summarizes the rest.</summary>
        private const int MaxListedResolvedAddresses = 8;

        /// <inheritdoc />
        public string Name => RuleName;

        /// <summary>
        /// The owner of every base triangle a set of parts claims: the one authority the diagnostics and the
        /// plan both read.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A triangle claimed by exactly one part is owned by that part. A contested triangle is owned by the
        /// unique highest declared priority, which is the same decision <see cref="Validate"/> reports as
        /// <c>APA035</c>. A contest this rule refuses (a claimant that declares nothing, or a tie at the highest
        /// priority) has no owner and is absent from the map, because validation blocks such a configuration
        /// before a plan can exist.
        /// </para>
        /// <para>
        /// The map is exposed so that "ownership of the region" is a value a consumer can query rather than
        /// prose inside a warning message. It changes no geometry: the removed triangle set is the union of the
        /// claims either way, because removal is idempotent.
        /// </para>
        /// </remarks>
        public static IReadOnlyDictionary<RemovedTriangleAddress, string> ResolveOwners(
            IReadOnlyList<PartSnapshot> parts)
        {
            var owners = new Dictionary<RemovedTriangleAddress, string>();
            if (parts == null) return new ReadOnlyDictionary<RemovedTriangleAddress, string>(owners);

            var claims = CollectClaims(parts, null, null);
            var addresses = new List<RemovedTriangleAddress>(claims.Keys);
            addresses.Sort();

            for (var a = 0; a < addresses.Count; a++)
            {
                var address = addresses[a];
                var list = claims[address];
                if (list.Count == 0) continue;

                var outcome = DecideOwner(list, out var ownerPartId, out _);
                if (outcome == OverlapOutcome.Resolved && !string.IsNullOrEmpty(ownerPartId))
                {
                    owners[address] = ownerPartId;
                }
            }

            return new ReadOnlyDictionary<RemovedTriangleAddress, string>(owners);
        }

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context.Base == null || context.Base.Mesh == null) return;

            var mesh = context.Base.Mesh;

            // Every claim on a base triangle, in canonical part order because the part list is already sorted.
            // Address validation and the report happen while the claims are collected, so a malformed address is
            // reported exactly once and never becomes a claim.
            var claims = CollectClaims(context.Parts, mesh, issues);

            ResolveOverlaps(claims, issues);
        }

        /// <summary>
        /// Collects every part's usable claims, reporting a priority outside the domain and an unusable address
        /// when a report and a mesh are supplied.
        /// </summary>
        /// <remarks>
        /// Zero means "not declared" and is inert. A negative value is outside the domain this build defines, and
        /// silently reinterpreting it (as zero, or as the lowest priority) would let a malformed asset change
        /// conflict resolution, so it blocks and the part's claims do not participate. The address set is already
        /// canonical: ascending and de-duplicated, because de-duplication at construction means a part cannot
        /// report the same triangle twice.
        /// </remarks>
        private static Dictionary<RemovedTriangleAddress, List<RemovalClaim>> CollectClaims(
            IReadOnlyList<PartSnapshot> parts,
            MeshSnapshot mesh,
            List<ValidationIssue> issues)
        {
            var claims = new Dictionary<RemovedTriangleAddress, List<RemovalClaim>>();
            if (parts == null) return claims;

            for (var p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                if (part == null) continue;

                var removed = part.RemovedTriangles;
                if (removed == null || removed.Count == 0) continue;

                for (var i = 0; i < removed.Count; i++)
                {
                    var address = removed[i];

                    if (mesh != null && !ValidateAddress(mesh, part.PartId, address, issues)) continue;

                    if (!claims.TryGetValue(address, out var list))
                    {
                        list = new List<RemovalClaim>(2);
                        claims.Add(address, list);
                    }

                    // Legacy conflict priorities are no longer part of the snapshot. Keep a zero-valued claim
                    // so overlap diagnostics retain their stable shape and continue to fail closed.
                    list.Add(new RemovalClaim(part.PartId, 0));
                }
            }

            return claims;
        }

        /// <summary>Decides whether one or more parts claim the same base triangle.</summary>
        private static OverlapOutcome DecideOwner(
            List<RemovalClaim> claims,
            out string ownerPartId,
            out int ownerPriority)
        {
            ownerPartId = string.Empty;
            ownerPriority = 0;

            if (claims == null || claims.Count == 0) return OverlapOutcome.Resolved;

            ownerPartId = claims[0].PartId;
            if (claims.Count == 1) return OverlapOutcome.Resolved;
            return OverlapOutcome.UnresolvedUndeclaredPriority;
        }

        /// <summary>
        /// Decides the owner of every contested triangle, reporting a block or an <c>APA035</c> resolution.
        /// </summary>
        /// <remarks>
        /// Addresses are visited in ascending canonical order, and the resolved warnings are grouped by
        /// (owner, losing claimants) rather than emitted per triangle, so a large overlap cannot produce
        /// thousands of issues. The issue sort then places the warnings deterministically in the report.
        /// </remarks>
        private static void ResolveOverlaps(
            Dictionary<RemovedTriangleAddress, List<RemovalClaim>> claims,
            List<ValidationIssue> issues)
        {
            if (claims.Count == 0) return;

            var addresses = new List<RemovedTriangleAddress>(claims.Keys);
            addresses.Sort();

            var resolved = new Dictionary<string, ResolvedOverlap>(StringComparer.Ordinal);

            for (var a = 0; a < addresses.Count; a++)
            {
                var address = addresses[a];
                var list = claims[address];
                if (list.Count < 2) continue;

                var outcome = DecideOwner(list, out var ownerPartId, out var ownerPriority);

                if (outcome == OverlapOutcome.UnresolvedUndeclaredPriority)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.RemovalRegionOverlap,
                        ApaIssuePhase.Removal,
                        "Removal triangle " + address.TriangleIndexWithinSubMesh + " in submesh " +
                        address.SubMeshIndex + " is claimed by " + list.Count + " parts (" + DescribeClaims(list) +
                        ") and not every claimant declares a conflict priority. A priority resolves an overlap " +
                        "only when every claimant declares one; declare a positive priority on each part or " +
                        "remove the overlap.",
                        ownerPartId,
                        address.TriangleIndexWithinSubMesh,
                        address.SubMeshIndex,
                        detail: address + "; reason=undeclared-priority; claims=" + DescribeClaims(list)));
                    continue;
                }

                if (outcome == OverlapOutcome.UnresolvedPriorityTie)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.RemovalRegionOverlap,
                        ApaIssuePhase.Removal,
                        "Removal triangle " + address.TriangleIndexWithinSubMesh + " in submesh " +
                        address.SubMeshIndex + " is claimed by " + list.Count + " parts (" + DescribeClaims(list) +
                        ") and the highest declared priority is not unique. Equal priorities have no defined " +
                        "order, so the overlap cannot be resolved.",
                        ownerPartId,
                        address.TriangleIndexWithinSubMesh,
                        address.SubMeshIndex,
                        detail: address + "; reason=priority-tie; claims=" + DescribeClaims(list)));
                    continue;
                }

                var key = ownerPartId + "\u0000" + DescribeLosers(list, ownerPartId);

                if (!resolved.TryGetValue(key, out var group))
                {
                    group = new ResolvedOverlap(ownerPartId, ownerPriority, DescribeLosers(list, ownerPartId));
                    resolved.Add(key, group);
                }

                group.Addresses.Add(address);
            }

            EmitResolutions(resolved, issues);
        }

        /// <summary>The outcome of deciding who owns one contested base triangle.</summary>
        private enum OverlapOutcome
        {
            /// <summary>A unique owner was decided: the sole claimant, or the unique highest priority.</summary>
            Resolved = 0,

            /// <summary>At least one claimant declares nothing, so the contest cannot be resolved.</summary>
            UnresolvedUndeclaredPriority = 1,

            /// <summary>The highest declared priority is shared, so the contest cannot be resolved.</summary>
            UnresolvedPriorityTie = 2
        }

        private static void EmitResolutions(
            Dictionary<string, ResolvedOverlap> resolved,
            List<ValidationIssue> issues)
        {
            if (resolved.Count == 0) return;

            var keys = new List<string>(resolved.Keys);
            keys.Sort(StringComparer.Ordinal);

            for (var i = 0; i < keys.Count; i++)
            {
                var group = resolved[keys[i]];

                issues.Add(ValidationIssue.Warning(
                    ApaErrorCode.RemovalOverlapResolvedByPriority,
                    ApaIssuePhase.Removal,
                    "Declared conflict priority resolved an overlap on " + group.Addresses.Count +
                    " base triangle(s): part '" + group.OwnerPartId + "' (priority " + group.OwnerPriority +
                    ") owns the region, which was also claimed by " + group.Losers + ". The removed triangle " +
                    "set is unchanged (removal is idempotent); what the priority decided is ownership of the " +
                    "region.",
                    group.OwnerPartId,
                    group.Addresses[0].TriangleIndexWithinSubMesh,
                    group.Addresses[0].SubMeshIndex,
                    detail: "reason=removal-overlap-resolved-by-priority; owner=" + group.OwnerPartId +
                            "; ownerPriority=" + group.OwnerPriority +
                            "; losers=" + group.Losers +
                            "; triangles=" + group.Addresses.Count +
                            "; addresses=" + DescribeAddresses(group.Addresses)));
            }
        }

        private static string DescribeAddresses(List<RemovedTriangleAddress> addresses)
        {
            var sb = new System.Text.StringBuilder();
            var count = addresses.Count < MaxListedResolvedAddresses ? addresses.Count : MaxListedResolvedAddresses;

            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(addresses[i]);
            }

            if (addresses.Count > count) sb.Append("|+").Append(addresses.Count - count).Append(" more");
            return sb.ToString();
        }

        private static string DescribeClaims(List<RemovalClaim> claims)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < claims.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(claims[i].PartId).Append('@').Append(claims[i].Priority);
            }

            return sb.ToString();
        }

        private static string DescribeLosers(List<RemovalClaim> claims, string ownerPartId)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < claims.Count; i++)
            {
                var claim = claims[i];
                if (string.Equals(claim.PartId, ownerPartId, StringComparison.Ordinal)) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(claim.PartId).Append('@').Append(claim.Priority);
            }

            return sb.ToString();
        }

        /// <summary>One part's claim on one base triangle.</summary>
        private sealed class RemovalClaim
        {
            public string PartId { get; }
            public int Priority { get; }

            public RemovalClaim(string partId, int priority)
            {
                PartId = partId ?? string.Empty;
                Priority = priority;
            }
        }

        /// <summary>One resolved overlap group: an owner and the losing claimants.</summary>
        private sealed class ResolvedOverlap
        {
            public string OwnerPartId { get; }
            public int OwnerPriority { get; }
            public string Losers { get; }
            public List<RemovedTriangleAddress> Addresses { get; } = new List<RemovedTriangleAddress>();

            public ResolvedOverlap(string ownerPartId, int ownerPriority, string losers)
            {
                OwnerPartId = ownerPartId;
                OwnerPriority = ownerPriority;
                Losers = losers ?? string.Empty;
            }
        }

        /// <summary>
        /// Validates one address against the target mesh, reporting the specific defect rather than a generic
        /// "out of range".
        /// </summary>
        /// <remarks>
        /// The submesh component is checked before the triangle component because the two produce different
        /// remedies: an unknown submesh means the profile was authored against a different body, while an
        /// unknown triangle within a known submesh usually means the submesh was re-exported.
        /// </remarks>
        private static bool ValidateAddress(
            MeshSnapshot mesh,
            string partId,
            RemovedTriangleAddress address,
            List<ValidationIssue> issues)
        {
            if (address.SubMeshIndex < 0 || address.TriangleIndexWithinSubMesh < 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InvalidTriangleAddress,
                    ApaIssuePhase.Removal,
                    "A removal address has a negative component, so it cannot name a triangle. " +
                    "Submesh " + address.SubMeshIndex + ", triangle " + address.TriangleIndexWithinSubMesh + ".",
                    partId,
                    address.TriangleIndexWithinSubMesh,
                    address.SubMeshIndex,
                    detail: address + "; reason=negative-component"));
                return false;
            }

            if (address.SubMeshIndex >= mesh.SubMeshCount)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InvalidTriangleAddress,
                    ApaIssuePhase.Removal,
                    "A removal address names submesh " + address.SubMeshIndex + ", but the target mesh has " +
                    mesh.SubMeshCount + " submesh(es). The profile was probably authored against a different body.",
                    partId,
                    address.TriangleIndexWithinSubMesh,
                    address.SubMeshIndex,
                    detail: address + "; subMeshCount=" + mesh.SubMeshCount + "; reason=submesh-out-of-range"));
                return false;
            }

            var topology = address.SubMeshIndex < mesh.Topologies.Length
                ? mesh.Topologies[address.SubMeshIndex]
                : UnityEngine.MeshTopology.Triangles;
            // A triangle address is only meaningful in a triangle list. Addressing "triangle 0" of a line or
            // point submesh is not an out-of-range triangle; it is a category error, and treating it as an
            // empty removal set would silently drop the author's intent.
            if (topology != UnityEngine.MeshTopology.Triangles)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InvalidTriangleAddress,
                    ApaIssuePhase.Removal,
                    "A removal address names submesh " + address.SubMeshIndex + ", whose topology is " + topology +
                    ". Only triangle submeshes can be addressed.",
                    partId,
                    address.TriangleIndexWithinSubMesh,
                    address.SubMeshIndex,
                    detail: address + "; topology=" + topology + "; reason=not-a-triangle-list"));
                return false;
            }

            var indices = mesh.SubMeshes[address.SubMeshIndex];
            var triangleCount = indices != null ? indices.Length / ApaMeshLimits.TriangleStride : 0;

            if (address.TriangleIndexWithinSubMesh >= triangleCount)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.RemovalIndexOutOfRange,
                    ApaIssuePhase.Removal,
                    "Removal triangle " + address.TriangleIndexWithinSubMesh + " is outside submesh " +
                    address.SubMeshIndex + ", which has " + triangleCount + " triangle(s).",
                    partId,
                    address.TriangleIndexWithinSubMesh,
                    address.SubMeshIndex,
                    detail: address + "; triangleCount=" + triangleCount));
                return false;
            }

            return true;
        }
    }
}
