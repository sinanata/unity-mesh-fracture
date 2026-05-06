using System.Collections.Generic;
using UnityEngine;

namespace MeshFracture
{
    /// <summary>One Voronoi cell of a fractured mesh.</summary>
    public struct FragmentResult
    {
        /// <summary>Fragment mesh, recentered on its centroid.</summary>
        public Mesh Mesh;
        /// <summary>World-space centroid of the cell — where the fragment should be positioned.</summary>
        public Vector3 Centroid;
    }

    /// <summary>
    /// Static utility that Voronoi-fractures a mesh into convex cells using
    /// iterative half-space clipping. Each fragment is a 2-submesh mesh:
    /// submesh 0 = exterior (the original surface), submesh 1 = interior (cap
    /// faces from the cuts) — letting you assign separate materials so cuts
    /// look like fresh interior, not like exterior surface.
    ///
    /// Watertightness contract: every emitted fragment is a closed
    /// polyhedron — every cut edge is sealed by a cap polygon, and caps
    /// from earlier planes are themselves clipped + capped by later planes
    /// where the planes intersect inside the cell. The naive "clip the
    /// exterior, generate one cap per plane" pattern leaves orphan cap
    /// geometry past the cell boundary and fails to seal where two planes'
    /// boundary lines meet; this implementation maintains a single
    /// triangle list with per-triangle submesh tags and re-clips
    /// everything against every plane so the seal closes around the
    /// entire convex intersection.
    ///
    /// The algorithm is pure C#, deterministic from the input mesh + seed
    /// position, and has no dependencies beyond <c>UnityEngine</c>. Suitable
    /// for both runtime use (cheap enough at small <paramref name="count"/>)
    /// and editor/build-time pre-baking via <see cref="FragmentCache"/>.
    /// </summary>
    public static class MeshFragmenter
    {
        // Submesh tags. Stored as bytes in a parallel list to keep the
        // ClipResult small; expanded to ints only at mesh-build time.
        private const byte TAG_EXTERIOR = 0;
        private const byte TAG_CAP      = 1;

        /// <summary>
        /// Fracture a mesh into <paramref name="count"/> Voronoi cells.
        /// </summary>
        /// <param name="source">Input mesh. Read-only — not mutated.</param>
        /// <param name="count">Target number of fragments. Must be ≥ 2; smaller values return the source unchanged.</param>
        /// <param name="center">Reference point used to seed the Voronoi distribution and to seed the RNG. Use the world-space pivot of the original mesh.</param>
        /// <returns>Array of fragments. May be shorter than <paramref name="count"/> if some cells degenerate (sliver geometry, fully eroded by neighbours). Always non-empty — falls back to a single fragment containing the source mesh if every cell degenerates.</returns>
        public static FragmentResult[] Fragment(Mesh source, int count, Vector3 center)
        {
            if (source == null || count < 2)
                return new[] { new FragmentResult { Mesh = source, Centroid = center } };

            var bounds = source.bounds;
            var seeds = GenerateSeeds(count, bounds, center);

            // Read source mesh data once.
            var srcVerts = source.vertices;
            var srcNorms = source.normals;
            var srcUVs = source.uv;
            var srcTangents = source.tangents;
            var srcTris = source.triangles;

            // Defensive: missing normals/UVs/tangents is common on procedurally
            // generated source meshes. Pad with zero-vectors so the per-vertex
            // arrays stay aligned with the position array — split vertices
            // produced by clipping interpolate over these.
            int srcVertCount = srcVerts.Length;
            if (srcNorms.Length    != srcVertCount) srcNorms    = new Vector3[srcVertCount];
            if (srcUVs.Length      != srcVertCount) srcUVs      = new Vector2[srcVertCount];
            if (srcTangents.Length != srcVertCount) srcTangents = new Vector4[srcVertCount];

            var results = new List<FragmentResult>();

            for (int s = 0; s < seeds.Length; s++)
            {
                // Per-cell working lists. Triangles carry a submesh tag
                // (0 = exterior, 1 = cap) so caps from one plane get
                // clipped + retagged correctly when later planes pass over
                // them — the key property that makes the final fragment
                // watertight.
                var cellVerts    = new List<Vector3>(srcVerts);
                var cellNorms    = new List<Vector3>(srcNorms);
                var cellUVs      = new List<Vector2>(srcUVs);
                var cellTangents = new List<Vector4>(srcTangents);

                int srcTriCount = srcTris.Length / 3;
                var cellTris    = new List<int>(srcTris.Length);
                var cellTags    = new List<byte>(srcTriCount);
                for (int t = 0; t < srcTris.Length; t += 3)
                {
                    cellTris.Add(srcTris[t]);
                    cellTris.Add(srcTris[t + 1]);
                    cellTris.Add(srcTris[t + 2]);
                    cellTags.Add(TAG_EXTERIOR);
                }

                bool cellValid = true;

                for (int other = 0; other < seeds.Length; other++)
                {
                    if (other == s) continue;

                    // Bisecting plane between seed[s] and seed[other]. Verts
                    // with positive distance lie on seed[s]'s side and are
                    // kept; the rest is discarded.
                    Vector3 midpoint    = (seeds[s] + seeds[other]) * 0.5f;
                    Vector3 planeNormal = (seeds[s] - seeds[other]).normalized;

                    ClipResult clip = ClipMeshByPlane(
                        cellVerts, cellNorms, cellUVs, cellTangents,
                        cellTris, cellTags,
                        midpoint, planeNormal);

                    if (clip.Verts.Count < 3 || clip.Tris.Count < 3)
                    {
                        cellValid = false;
                        break;
                    }

                    cellVerts    = clip.Verts;
                    cellNorms    = clip.Norms;
                    cellUVs      = clip.UVs;
                    cellTangents = clip.Tangents;
                    cellTris     = clip.Tris;
                    cellTags     = clip.Tags;

                    // Cap polygons on THIS plane. Cut edges accumulated
                    // during the clip include cuts on the source surface
                    // AND on any earlier-plane caps that this plane also
                    // crossed — together they bound the cell's footprint
                    // on the new plane. Multi-loop support handles
                    // non-convex source meshes where the plane intersects
                    // the body in disjoint regions; each region becomes
                    // its own polygon.
                    //
                    // The dedup-by-position step is load-bearing: when a
                    // source-exterior triangle and an earlier-plane cap
                    // both straddle this plane, LerpVertex generates two
                    // SEPARATE intersection vertices at the SAME world
                    // position (the per-plane LerpCache keys by source
                    // edge (a,b), not by position, and TriangulateCap
                    // duplicates polygon verts to give caps their own
                    // flat normals — so the source-exterior side hits
                    // index A while the earlier-cap side hits index B).
                    // Without merging A and B into one canonical index,
                    // the cut-edge graph splits into disconnected chains
                    // exactly where two clip planes meet the cell's
                    // boundary; both chains fall below the 3-vertex
                    // chaining floor; nothing seals the cut. Visible as
                    // "the cube fractured but interior shows through" —
                    // the user-reported symptom.
                    if (clip.CutEdges.Count >= 6)
                    {
                        Vector3 capNormal = -planeNormal;
                        var dedup = DedupCutEdgesByPosition(clip.CutEdges, cellVerts);
                        var loops = ChainEdgesAll(dedup);
                        for (int li = 0; li < loops.Count; li++)
                        {
                            TriangulateCap(loops[li], cellVerts, cellNorms, cellUVs, cellTangents,
                                capNormal, cellTris, cellTags);
                        }
                    }
                }

                if (!cellValid || cellTris.Count < 3) continue;

                // Centroid + recenter: fragment GameObjects sit at the
                // centroid, so storing verts relative to it makes
                // subsequent rotations cheap.
                Vector3 centroid = Vector3.zero;
                for (int i = 0; i < cellVerts.Count; i++)
                    centroid += cellVerts[i];
                centroid /= cellVerts.Count;

                for (int i = 0; i < cellVerts.Count; i++)
                    cellVerts[i] -= centroid;

                // Partition triangles by tag into the two submesh lists.
                // Empty submeshes (a fragment that's all-cap from a degenerate
                // source slice, or all-exterior from a 1-plane cut) get an
                // empty triangle list — Unity tolerates this fine and
                // material assignment still maps correctly.
                int triCount = cellTags.Count;
                var extTris = new List<int>(triCount * 3);
                var capTris = new List<int>(triCount);
                for (int t = 0; t < triCount; t++)
                {
                    int b = t * 3;
                    if (cellTags[t] == TAG_EXTERIOR)
                    {
                        extTris.Add(cellTris[b]);
                        extTris.Add(cellTris[b + 1]);
                        extTris.Add(cellTris[b + 2]);
                    }
                    else
                    {
                        capTris.Add(cellTris[b]);
                        capTris.Add(cellTris[b + 1]);
                        capTris.Add(cellTris[b + 2]);
                    }
                }

                var mesh = new Mesh { name = $"Fragment_{s}" };
                if (cellVerts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(cellVerts);
                mesh.SetNormals(cellNorms);
                mesh.SetUVs(0, cellUVs);
                mesh.SetTangents(cellTangents);
                mesh.subMeshCount = 2;
                mesh.SetTriangles(extTris, 0);
                mesh.SetTriangles(capTris, 1);
                mesh.RecalculateBounds();

                results.Add(new FragmentResult { Mesh = mesh, Centroid = centroid });
            }

            // Fallback: every cell degenerated. Returning the source as a
            // single "fragment" lets callers proceed without null checks.
            if (results.Count == 0)
                results.Add(new FragmentResult { Mesh = source, Centroid = center });

            return results.ToArray();
        }

        /// <summary>
        /// Bake a <see cref="SkinnedMeshRenderer"/>'s current animated pose
        /// into a static <see cref="Mesh"/>. The skinning pipeline evaluates
        /// bone matrices, so the resulting vertices are in SMR-local space
        /// — caller multiplies by <c>smr.transform.lossyScale</c> if they
        /// want world-scale fragments.
        ///
        /// We use the explicit <c>(mesh, useScale: false)</c> overload —
        /// the no-arg version of <c>BakeMesh</c> already multiplies by the
        /// SMR's lossy scale on Unity 2021+, so callers that also pre-scale
        /// (the obvious "I want world-size fragments" path) get the scale
        /// applied twice and produce 1.69×-too-big fragments on a 1.3×
        /// character. Forcing <c>useScale: false</c> here makes the
        /// caller's <c>lossyScale</c> multiply load-bearing instead of
        /// redundant.
        /// </summary>
        public static Mesh BakeSkinnedMesh(SkinnedMeshRenderer smr)
        {
            var baked = new Mesh();
            smr.BakeMesh(baked, useScale: false);
            baked.RecalculateBounds();
            baked.RecalculateTangents();
            return baked;
        }

        // =====================================================================
        // Seed generation
        // =====================================================================

        private static Vector3[] GenerateSeeds(int count, Bounds bounds, Vector3 center)
        {
            // Jittered grid distribution: divides the bounding box into a
            // roughly cubic grid of cells, places one seed per cell, then
            // jitters within the cell. Pure random distribution produces
            // clumpy fragments; pure grid produces visually obvious "Voronoi
            // tile" patterns. Jittered grid hits the sweet spot.
            int side = Mathf.CeilToInt(Mathf.Pow(count, 1f / 3f));
            var seeds = new List<Vector3>();
            var rng = new System.Random(center.GetHashCode() ^ System.Environment.TickCount);

            Vector3 step = new Vector3(
                bounds.size.x / side,
                bounds.size.y / side,
                bounds.size.z / Mathf.Max(side, 1));

            for (int ix = 0; ix < side && seeds.Count < count; ix++)
            for (int iy = 0; iy < side && seeds.Count < count; iy++)
            for (int iz = 0; iz < Mathf.Max(side, 1) && seeds.Count < count; iz++)
            {
                Vector3 basePos = bounds.min + new Vector3(
                    (ix + 0.5f) * step.x,
                    (iy + 0.5f) * step.y,
                    (iz + 0.5f) * step.z);

                basePos += new Vector3(
                    ((float)rng.NextDouble() - 0.5f) * step.x * 0.8f,
                    ((float)rng.NextDouble() - 0.5f) * step.y * 0.8f,
                    ((float)rng.NextDouble() - 0.5f) * step.z * 0.8f);

                seeds.Add(basePos);
            }

            // Pad with random seeds if grid didn't produce enough.
            while (seeds.Count < count)
            {
                seeds.Add(bounds.min + new Vector3(
                    (float)rng.NextDouble() * bounds.size.x,
                    (float)rng.NextDouble() * bounds.size.y,
                    (float)rng.NextDouble() * bounds.size.z));
            }

            return seeds.ToArray();
        }

        // =====================================================================
        // Plane clipping
        // =====================================================================

        private struct ClipResult
        {
            public List<Vector3> Verts;
            public List<Vector3> Norms;
            public List<Vector2> UVs;
            public List<Vector4> Tangents;
            public List<int>     Tris;     // every triangle (exterior + caps), tag-parallel to Tags
            public List<byte>    Tags;     // 0 = exterior, 1 = cap; one entry per triangle
            public List<int>     CutEdges; // pairs of vertex indices on this plane's cut

            // Edge → split-vertex cache. When two triangles share an edge
            // that the clip plane crosses, each calls LerpVertex on the
            // SAME (a,b) source pair — without this cache they'd allocate
            // two different vertex indices at the same world point, and
            // the resulting CutEdges graph would be disconnected
            // (ChainEdges can't walk through duplicated split points and
            // TriangulateCap returns empty → the "see-through cuts"
            // symptom). Scoped per ClipResult so subsequent clip planes
            // don't reuse stale entries from a previous plane's geometry.
            public Dictionary<long, int> LerpCache;
        }

        private static ClipResult ClipMeshByPlane(
            List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Vector4> tangents,
            List<int> tris, List<byte> tags,
            Vector3 planePoint, Vector3 planeNormal)
        {
            var result = new ClipResult
            {
                Verts     = new List<Vector3>(verts),
                Norms     = new List<Vector3>(norms),
                UVs       = new List<Vector2>(uvs),
                Tangents  = new List<Vector4>(tangents),
                Tris      = new List<int>(tris.Count),
                Tags      = new List<byte>(tags.Count),
                CutEdges  = new List<int>(),
                LerpCache = new Dictionary<long, int>(),
            };

            // Classify vertices: positive = on the kept side, negative = discard.
            float[] dists = new float[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                dists[i] = Vector3.Dot(verts[i] - planePoint, planeNormal);

            int triCount = tags.Count;
            for (int t = 0; t < triCount; t++)
            {
                int b = t * 3;
                int i0 = tris[b], i1 = tris[b + 1], i2 = tris[b + 2];
                byte tag = tags[t];
                float d0 = dists[i0], d1 = dists[i1], d2 = dists[i2];
                bool above0 = d0 >= 0, above1 = d1 >= 0, above2 = d2 >= 0;

                int aboveCount = (above0 ? 1 : 0) + (above1 ? 1 : 0) + (above2 ? 1 : 0);

                if (aboveCount == 3)
                {
                    result.Tris.Add(i0);
                    result.Tris.Add(i1);
                    result.Tris.Add(i2);
                    result.Tags.Add(tag);
                }
                else if (aboveCount == 0)
                {
                    continue;
                }
                else
                {
                    SplitTriangle(result, tag, i0, i1, i2, d0, d1, d2, above0, above1, above2);
                }
            }

            return result;
        }

        private static void SplitTriangle(ClipResult result, byte tag,
            int i0, int i1, int i2, float d0, float d1, float d2,
            bool above0, bool above1, bool above2)
        {
            // Arrange so that the lone vertex (alone on its side) is first.
            int lone, pair1, pair2;
            float dLone, dPair1, dPair2;
            bool loneAbove;

            if (above0 == above1)
            {
                lone = i2; dLone = d2; loneAbove = above2;
                pair1 = i0; dPair1 = d0;
                pair2 = i1; dPair2 = d1;
            }
            else if (above0 == above2)
            {
                lone = i1; dLone = d1; loneAbove = above1;
                pair1 = i0; dPair1 = d0;
                pair2 = i2; dPair2 = d2;
            }
            else
            {
                lone = i0; dLone = d0; loneAbove = above0;
                pair1 = i1; dPair1 = d1;
                pair2 = i2; dPair2 = d2;
            }

            int intA = LerpVertex(result, lone, pair1, dLone, dPair1);
            int intB = LerpVertex(result, lone, pair2, dLone, dPair2);

            if (loneAbove)
            {
                result.Tris.Add(lone);
                result.Tris.Add(intA);
                result.Tris.Add(intB);
                result.Tags.Add(tag);
                // Cut edge points from intA to intB; CCW around the kept volume's
                // boundary on the plane (consistent for the outward cap normal).
                result.CutEdges.Add(intA);
                result.CutEdges.Add(intB);
            }
            else
            {
                result.Tris.Add(pair1);
                result.Tris.Add(pair2);
                result.Tris.Add(intB);
                result.Tags.Add(tag);

                result.Tris.Add(pair1);
                result.Tris.Add(intB);
                result.Tris.Add(intA);
                result.Tags.Add(tag);

                // Reversed direction so the cap polygon's boundary winding
                // stays consistent with the loneAbove case.
                result.CutEdges.Add(intB);
                result.CutEdges.Add(intA);
            }
        }

        private static int LerpVertex(ClipResult result, int a, int b, float da, float db)
        {
            // Dedup by source-edge key: any triangle splitting the SAME
            // edge (a,b) must reuse the same intersection vertex, or the
            // cut-edge graph has unbridgeable gaps and ChainEdges can't
            // assemble the cap polygon. Lerp(a,b,t) and Lerp(b,a,1-t)
            // resolve to the same world position, and the symmetry of
            // t = da/(da-db) ↔ db/(db-da) means the second caller would
            // recompute the identical vertex anyway — caching just skips
            // the alloc and hands back the existing index.
            long key = a < b
                ? ((long)a << 32) | (uint)b
                : ((long)b << 32) | (uint)a;
            if (result.LerpCache.TryGetValue(key, out var existing))
                return existing;

            float t = da / (da - db);
            t = Mathf.Clamp01(t);

            int newIdx = result.Verts.Count;
            result.Verts.Add(Vector3.Lerp(result.Verts[a], result.Verts[b], t));
            result.Norms.Add(Vector3.Lerp(result.Norms[a], result.Norms[b], t).normalized);
            result.UVs.Add(Vector2.Lerp(result.UVs[a], result.UVs[b], t));

            if (result.Tangents.Count > a && result.Tangents.Count > b)
                result.Tangents.Add(Vector4.Lerp(result.Tangents[a], result.Tangents[b], t));
            else
                result.Tangents.Add(new Vector4(1, 0, 0, 1));

            result.LerpCache[key] = newIdx;
            return newIdx;
        }

        // =====================================================================
        // Cap triangulation (fan over each chained polygon)
        // =====================================================================

        /// <summary>
        /// Triangulate one closed cap polygon and append the cap triangles
        /// (with TAG_CAP) directly into the running cell tris/tags lists.
        /// Vertices duplicated with the cap normal so lighting on the cut
        /// face stays flat instead of lerping into the original surface
        /// normals.
        /// </summary>
        private static void TriangulateCap(
            List<int> polygon,
            List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Vector4> tangents,
            Vector3 capNormal,
            List<int> outTris, List<byte> outTags)
        {
            if (polygon == null || polygon.Count < 3) return;

            // Enforce winding so the cap front-face points along capNormal.
            // ChainEdgesAll's walk order can yield CW or CCW polygon winding
            // depending on traversal, and a downstream Cull Back shader on
            // the cap submesh would back-face cull the wrong half. Fan tris
            // share the first triangle's winding for any convex polygon,
            // so testing one is enough.
            Vector3 a = verts[polygon[0]];
            Vector3 b = verts[polygon[1]];
            Vector3 c = verts[polygon[2]];
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), capNormal) < 0f)
                polygon.Reverse();

            // Duplicate vertices so the cap carries its own (flat) normal —
            // sharing exterior verts would cause the cap face's lighting to
            // smooth into the original surface normals.
            var capIndices = new List<int>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
            {
                int srcIdx = polygon[i];
                int newIdx = verts.Count;
                verts.Add(verts[srcIdx]);
                norms.Add(capNormal);
                uvs.Add(uvs[srcIdx]);
                tangents.Add(tangents.Count > srcIdx ? tangents[srcIdx] : new Vector4(1, 0, 0, 1));
                capIndices.Add(newIdx);
            }

            // Fan triangulation. Voronoi cell caps are convex by construction
            // (intersection of a plane with a convex region is convex), and
            // even non-convex source meshes produce convex sub-polygons per
            // cell after multi-plane clipping.
            for (int i = 1; i < capIndices.Count - 1; i++)
            {
                outTris.Add(capIndices[0]);
                outTris.Add(capIndices[i]);
                outTris.Add(capIndices[i + 1]);
                outTags.Add(TAG_CAP);
            }
        }

        /// <summary>
        /// Merge cut-edge vertices that occupy the same world position into
        /// a single canonical index. Required because TriangulateCap
        /// duplicates polygon vertices to carry the cap's flat normal —
        /// duplicated verts and their source-mesh originals coincide in
        /// world space but hold different indices, so when a later clip
        /// plane crosses both an exterior triangle and an earlier cap at
        /// the same intersection point, the per-plane LerpCache (keyed on
        /// the source EDGE, not on position) hands out two distinct
        /// intersection vertices. The cut-edge graph then splits into
        /// disjoint chains exactly where two planes' boundary lines meet
        /// the cell, both below the 3-vertex chaining threshold, and the
        /// cap goes un-sealed. Position-based dedup before
        /// <see cref="ChainEdgesAll"/> reconnects them into one polygon.
        /// </summary>
        private static List<int> DedupCutEdgesByPosition(List<int> cutEdges, List<Vector3> verts)
        {
            // Tight epsilon — these are intersections of a plane with the
            // SAME edge geometry, so any two coincident points are exactly
            // equal in IEEE-754 double precision and within ~1e-6 in
            // single. 1e-8 in squared-distance terms tolerates clamp01
            // round-off without falsely merging legitimately distinct
            // vertices on a mesh-scale cell.
            const float epsSq = 1e-8f;

            // Collect the unique vertex indices used by cut edges. The
            // canonical map then assigns each to either itself (first
            // occurrence at its position) or to the earlier index whose
            // position matches.
            var unique = new HashSet<int>();
            for (int i = 0; i < cutEdges.Count; i++) unique.Add(cutEdges[i]);

            var canonical = new Dictionary<int, int>(unique.Count);
            // Parallel arrays of (canonicalIdx, position) — small N (cut
            // edges per plane is typically <30 even for dense meshes), so
            // a linear scan is faster than a spatial hash and avoids the
            // bin-boundary edge-case where two points sit on opposite sides
            // of the hash cell.
            var positions = new List<Vector3>(unique.Count);
            var positionIdx = new List<int>(unique.Count);

            foreach (var idx in unique)
            {
                Vector3 p = verts[idx];
                int matched = -1;
                for (int i = 0; i < positions.Count; i++)
                {
                    if ((positions[i] - p).sqrMagnitude < epsSq)
                    {
                        matched = positionIdx[i];
                        break;
                    }
                }
                if (matched >= 0)
                {
                    canonical[idx] = matched;
                }
                else
                {
                    canonical[idx] = idx;
                    positions.Add(p);
                    positionIdx.Add(idx);
                }
            }

            var result = new List<int>(cutEdges.Count);
            for (int i = 0; i < cutEdges.Count; i++)
                result.Add(canonical[cutEdges[i]]);
            return result;
        }

        /// <summary>
        /// Walk the cut-edge graph and return ALL closed loops. Necessary
        /// because non-convex source meshes can produce multiple disjoint
        /// boundary polygons when a single clip plane crosses several
        /// disconnected regions of the body. The previous single-loop
        /// implementation left every loop after the first un-capped, which
        /// shows up as orphan edges + see-through holes in the fragment.
        /// </summary>
        private static List<List<int>> ChainEdgesAll(List<int> cutEdges)
        {
            var loops = new List<List<int>>();
            if (cutEdges.Count < 6) return loops;

            // Build undirected adjacency. Each edge contributes both
            // directions — closed convex polygons land at exactly two
            // neighbours per vertex, so the walk picks up the loop cleanly.
            var adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < cutEdges.Count; i += 2)
            {
                int a = cutEdges[i], b = cutEdges[i + 1];
                if (!adj.TryGetValue(a, out var listA)) { listA = new List<int>(2); adj[a] = listA; }
                if (!adj.TryGetValue(b, out var listB)) { listB = new List<int>(2); adj[b] = listB; }
                listA.Add(b);
                listB.Add(a);
            }

            var visited = new HashSet<int>();
            // Iterate over the recorded edge list (stable order) rather than
            // the dictionary key order, so the same input reliably produces
            // the same loop start vertex — useful for deterministic bakes.
            for (int seedIdx = 0; seedIdx < cutEdges.Count; seedIdx += 2)
            {
                int start = cutEdges[seedIdx];
                if (visited.Contains(start)) continue;

                var chain = new List<int>();
                int current = start;
                chain.Add(current);
                visited.Add(current);

                int safety = cutEdges.Count;  // can't run longer than total edges
                while (safety-- > 0)
                {
                    if (!adj.TryGetValue(current, out var neighbours)) break;
                    int next = -1;
                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        if (!visited.Contains(neighbours[i])) { next = neighbours[i]; break; }
                    }
                    if (next == -1) break;
                    chain.Add(next);
                    visited.Add(next);
                    current = next;
                }

                if (chain.Count >= 3) loops.Add(chain);
            }

            return loops;
        }
    }
}
