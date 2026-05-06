using MeshFracture;
using UnityEngine;

namespace MeshFractureDemo
{
    /// <summary>
    /// Per-target controller. Wraps one fracturable object on the demo stage:
    /// pre-bakes its Voronoi cells via <see cref="FragmentCache"/>, hides the
    /// visual on <see cref="Fracture"/>, spawns a <see cref="FractureBurst"/>,
    /// and restores the visual on <see cref="Restore"/>. The bootstrap creates
    /// one of these per <see cref="DemoTargetCatalog.Definition"/>.
    /// </summary>
    public class DemoTarget : MonoBehaviour
    {
        public DemoTargetCatalog.Definition Definition { get; private set; }
        public bool IsFractured { get; private set; }

        DemoBootstrap _bootstrap;
        Renderer[] _renderers;
        Material _interiorMaterial;
        FractureBurst _activeBurst;

        // Bake key state so we can re-bake when the user changes fragment count.
        int _bakedKey;
        int _bakedCount;

        public void Initialize(DemoTargetCatalog.Definition def, DemoBootstrap bootstrap)
        {
            Definition = def;
            _bootstrap = bootstrap;
            _renderers = GetComponentsInChildren<Renderer>(true);
            _interiorMaterial = MaterialFactory.InteriorMaterial(def.InteriorColor);
            RequestBake(_bootstrap.FragmentCount);
        }

        // =================================================================
        // Public API
        // =================================================================

        public void Fracture()
        {
            if (IsFractured) return;
            int count = _bootstrap.FragmentCount;
            if (count != _bakedCount)
            {
                // The user changed the slider since the last bake — re-bake
                // synchronously so this click can complete without a pop.
                BakeSync(count);
            }
            if (!FragmentCache.TryGet(_bakedKey, out var cached))
            {
                // Bake hasn't finished yet (rare — only on the first click
                // after Start). Force a sync bake.
                BakeSync(count);
                if (!FragmentCache.TryGet(_bakedKey, out cached)) return;
            }

            IsFractured = true;
            SetRenderersEnabled(false);
            // Disable the click collider while fractured — the visual is
            // hidden, so leaving an invisible collider in place would make
            // physics-mode fragments bounce off "nothing" and would also
            // route taps to the now-empty target slot. Restore() flips it
            // back on.
            SetClickColliderEnabled(false);

            var pos = ResolveFractureOrigin();
            var fragments = BuildOffsetFragments(cached, pos);

            var burstGO = new GameObject($"Burst_{Definition.DisplayName}");
            burstGO.transform.position = pos;
            var burst = burstGO.AddComponent<FractureBurst>();
            burst.EnableTrails        = _bootstrap.TrailsEnabled;
            burst.UseUnityPhysics     = true;     // fragments bounce off ground / pedestals / wall
            burst.DissolveAfterSettle = true;     // dissolve only kicks in after motion stops
            burst.SettleHoldDuration  = 1.0f;     // beat at rest before fading
            burst.DissolveDuration    = 1.2f;
            burst.Lifetime            = 12f;      // safety cap if a fragment never sleeps
            // Hand the pre-cooked collider meshes from FragmentCache to the
            // burst so MeshCollider assignment skips Unity's convex-hull
            // cook on each fragment build frame. On dense source meshes
            // (kenney character) this is the difference between a 50–180 ms
            // per-fracture hitch and ≤10 ms.
            burst.PreBakedColliderMeshes = cached.ColliderMeshes;
            burst.Initialize(
                fragments,
                exteriorMaterial: ResolveExteriorMaterial(),
                interiorMaterial: new Material(_interiorMaterial),  // copy so dissolve PB doesn't smear
                explosionForce: _bootstrap.ExplosionForce,
                meshRotation: cached.MeshRotation
            );
            _activeBurst = burst;
        }

        /// <summary>
        /// True while the active burst is still spawning fragment GameObjects
        /// (FractureBurst's progressive build hasn't completed yet). Used by
        /// <see cref="DemoBootstrap.FractureAllSpread"/> to serialise bursts:
        /// kicking off the next target's Fracture before the current one
        /// finishes building stacks per-frame work and reintroduces the
        /// jitter the progressive build was meant to eliminate.
        /// </summary>
        public bool IsBurstStillBuilding =>
            _activeBurst != null && !_activeBurst.IsFullyInitialized;

        public void Restore()
        {
            if (_activeBurst != null)
            {
                Destroy(_activeBurst.gameObject);
                _activeBurst = null;
            }
            if (!IsFractured) return;
            IsFractured = false;
            SetRenderersEnabled(true);
            SetClickColliderEnabled(true);
        }

        public void OnFragmentCountChanged()
        {
            // Drop the previous bake, queue a fresh one. Active fragments keep
            // dissolving — no point ripping them out mid-animation.
            FragmentCache.Evict(_bakedKey);
            RequestBake(_bootstrap.FragmentCount);
        }

        // =================================================================
        // Bake helpers
        // =================================================================

        void RequestBake(int count)
        {
            _bakedCount = count;
            _bakedKey = ResolveBakeKey(count);

            // Solidify-for-fragment path: hollow geometry (open crates,
            // chests with thin walls) produces paper-thin shell fragments
            // because the carved source has no triangles between its inner
            // and outer surfaces. The fix MERGES the carved mesh with an
            // interior filler box so the fragmenter sees both the original
            // walls AND solid wood mass behind them — fragments come out
            // chunky while preserving the carved exterior silhouette.
            // Cheap enough on a small merged mesh that we use the
            // synchronous bake path; the cache lands by the time
            // RequestBake returns.
            if (Definition.SolidifyForFragment)
            {
                var srcMesh = ResolveSourceMesh();
                if (srcMesh != null)
                {
                    var solid = BuildSolidifiedMergedMesh(srcMesh, ResolveSourceScale());
                    if (solid != null)
                    {
                        FragmentCache.BakeSynchronous(
                            _bakedKey, solid, ResolveSourceRotation(), count);
                        return;
                    }
                }
                // Fall through to the async path if mesh lookup or merge
                // failed; better to ship thin-shell fragments than no
                // fragments at all.
            }

            // Both static and skinned go through the async cache so the
            // 8-target startup pre-bake spreads across frames instead of
            // stacking into the first scene-load tick. RequestPreBake's
            // SkinnedMeshRenderer branch reads sharedMesh + lossyScale —
            // the same bind-pose path BakeSkinnedSync took — so the
            // skinned character bakes identically to before, just on the
            // worker coroutine instead of inline. (BakeSkinnedSync stays
            // around as the sync fallback inside BakeSync for the rare
            // case where Fracture() is called on a target whose bake
            // isn't cached yet, e.g. mid-slider-change.)
            FragmentCache.RequestPreBake(_bakedKey, gameObject, count);
        }

        /// <summary>
        /// True once <see cref="FragmentCache"/> holds the fragments for
        /// the target's current FragmentCount. Polled by DemoBootstrap's
        /// FractureAll coroutine so it can wait out the pre-bake before
        /// firing every burst at once.
        /// </summary>
        public bool IsBakeReady => FragmentCache.TryGet(_bakedKey, out _);

        void BakeSync(int count)
        {
            _bakedCount = count;
            _bakedKey = ResolveBakeKey(count);

            if (Definition.IsSkinned) { BakeSkinnedSync(count); return; }

            var srcMesh = ResolveSourceMesh();
            if (srcMesh == null) return;
            var rot   = ResolveSourceRotation();
            var scale = ResolveSourceScale();

            // Hollow source meshes (carved crate, chest) get merged with
            // an interior filler box here too — same reasoning as
            // RequestBake's solidify branch. BuildSolidifiedMergedMesh
            // applies the lossy scale itself, so we hand the result
            // straight to the cache without ScaledMeshCopy. Non-solidified
            // meshes still go through the standard scale-then-fragment
            // path.
            Mesh fragmentSource = Definition.SolidifyForFragment
                ? BuildSolidifiedMergedMesh(srcMesh, scale)
                : ScaledMeshCopy(srcMesh, scale);

            if (fragmentSource == null) fragmentSource = ScaledMeshCopy(srcMesh, scale);
            FragmentCache.BakeSynchronous(_bakedKey, fragmentSource, rot, count);
        }

        void BakeSkinnedSync(int count)
        {
            var smr = GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr == null || smr.sharedMesh == null) return;

            // Bind-pose path (mirrors LoL's FragmentCache). Reads the SMR's
            // mesh-local sharedMesh and multiplies by smr.transform.lossyScale
            // to get world-equivalent vertices. Lossy-scale is, by Unity's
            // definition, the world-space factor at which the SMR renders
            // — so this product is correct for any prefab hierarchy.
            //
            // Why we don't use BakeMesh here: kenney FBXes bake scale 100
            // into multiple intermediate transforms (the editor inspector
            // shows both Root and characterMedium subobjects at scale 100,
            // visually-confirmed in characterMedium.fbx). When the bones
            // sit under one branch (Root) and the SMR sits on a sibling
            // branch (characterMedium), BakeMesh evaluates bone matrices
            // whose chain-scale isn't cancelled out by the SMR's
            // worldToLocal — and either useScale path leaks one of the
            // 100× factors into the output, which then multiplies again
            // when we apply lossyScale, producing fragments at ~13 000×
            // the intended size.
            //
            // Trade-off: fragments are always at the BIND POSE, not the
            // live animated pose. For a T-pose character (the demo's
            // case — no Animator playing), identical; for an animated
            // character, fragments wouldn't match the exact frame-of-death
            // pose but still read as the same silhouette. If a future
            // demo wants pose-aware fragmentation, gate this path on a
            // catalog flag rather than fighting BakeMesh's scale leak.
            var scaled = ScaledMeshCopy(smr.sharedMesh, smr.transform.lossyScale);

            FragmentCache.Evict(_bakedKey);
            FragmentCache.BakeSynchronous(_bakedKey, scaled, smr.transform.rotation, count);
        }

        // =================================================================
        // Source-mesh / material resolution
        // =================================================================

        Mesh ResolveSourceMesh()
        {
            var mf = GetComponentInChildren<MeshFilter>(true);
            return mf != null ? mf.sharedMesh : null;
        }

        Quaternion ResolveSourceRotation()
        {
            var mf = GetComponentInChildren<MeshFilter>(true);
            return mf != null ? mf.transform.rotation : transform.rotation;
        }

        Vector3 ResolveSourceScale()
        {
            var mf = GetComponentInChildren<MeshFilter>(true);
            return mf != null ? mf.transform.lossyScale : transform.lossyScale;
        }

        Vector3 ResolveFractureOrigin()
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return transform.position;
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b.center;
        }

        Material ResolveExteriorMaterial()
        {
            // Wrap the source material in URP/Lit (transparent + Cull Off) so the exterior
            // dissolves AND renders with Cull Off — that's what makes the
            // tumbling fragments read as solid chunks instead of papery
            // shells. See MaterialFactory.ExteriorFractureMaterial for the
            // longer "why" (mirrors LoL's DestructibleInstance pattern).
            Material source = null;
            foreach (var r in _renderers)
            {
                if (r != null && r.sharedMaterial != null) { source = r.sharedMaterial; break; }
            }
            return MaterialFactory.ExteriorFractureMaterial(source);
        }

        // =================================================================
        // Geometry helpers
        // =================================================================

        static Mesh ScaledMeshCopy(Mesh source, Vector3 scale)
        {
            // Object.Instantiate copies submeshes + bone weights — but bone
            // weights are irrelevant after BakeMesh has flattened the pose.
            // For static meshes, Instantiate is the simplest portable copy.
            var copy = Object.Instantiate(source);
            if (scale == Vector3.one) return copy;
            var verts = copy.vertices;
            for (int i = 0; i < verts.Length; i++) verts[i] = Vector3.Scale(verts[i], scale);
            copy.vertices = verts;
            copy.RecalculateBounds();
            return copy;
        }

        // Build a fragmentation source for hollow / carved geometry that
        // KEEPS the original outer silhouette (so wall fragments still
        // read as crate planks with carved detail) while ADDING solid
        // wood mass behind it (so fragments aren't just paper-thin
        // shells). The trick is a single merged mesh with two parts:
        //   1. The original carved geometry, scaled to world size.
        //   2. A 24-vert AABB filler box at 0.85× extents of the source,
        //      sat clearly inside the inner walls so it doesn't z-fight
        //      with them. UVs all set to the source's average UV so the
        //      box samples the same atlas swatch the carved walls use
        //      (wood for kenney crates).
        // The fragmenter operates on the union: Voronoi cells span
        // wall pieces + adjacent filler, so each cell has volume even
        // where the original carving has none. Pre-fracture visual
        // still uses the original mesh — the merged mesh is consumed
        // only by FragmentCache.BakeSynchronous.
        static Mesh BuildSolidifiedMergedMesh(Mesh source, Vector3 scale)
        {
            if (source == null) return null;

            // Scale to world size first; we work entirely in
            // scaled-mesh-local space from here on.
            var scaled = ScaledMeshCopy(source, scale);
            var bounds = scaled.bounds;
            if (bounds.size.sqrMagnitude < 1e-6f) return scaled;

            Vector3 fillerCenter   = bounds.center;
            Vector3 fillerHalfSize = bounds.extents * 0.85f;
            Vector2 fillerUV       = ComputeAverageUV(source);

            var filler = BuildAABBCubeMesh(fillerCenter, fillerHalfSize, fillerUV);
            return MergeMeshes(scaled, filler, source.name + "_Solidified");
        }

        // 24-vertex flat-shaded AABB cube at the given center/half-size,
        // every vertex carrying the same UV. Each face owns its own
        // 4-vertex group so normals stay flat across edges (sharing
        // corners would smooth the lighting and the cube would read as
        // a low-poly sphere). Pure helper — no Mesh.bounds dependency,
        // both the carved-source merge and any future "pure cube" path
        // can call it.
        static Mesh BuildAABBCubeMesh(Vector3 center, Vector3 halfSize, Vector2 uv)
        {
            Vector3 min = center - halfSize;
            Vector3 max = center + halfSize;

            Vector3 v000 = new Vector3(min.x, min.y, min.z);
            Vector3 v100 = new Vector3(max.x, min.y, min.z);
            Vector3 v010 = new Vector3(min.x, max.y, min.z);
            Vector3 v110 = new Vector3(max.x, max.y, min.z);
            Vector3 v001 = new Vector3(min.x, min.y, max.z);
            Vector3 v101 = new Vector3(max.x, min.y, max.z);
            Vector3 v011 = new Vector3(min.x, max.y, max.z);
            Vector3 v111 = new Vector3(max.x, max.y, max.z);

            var verts = new Vector3[24];
            var norms = new Vector3[24];
            var uvs   = new Vector2[24];
            var tris  = new int[36];
            int v = 0, t = 0;

            void AddFace(Vector3 a, Vector3 b1, Vector3 c, Vector3 d, Vector3 n)
            {
                verts[v + 0] = a;
                verts[v + 1] = b1;
                verts[v + 2] = c;
                verts[v + 3] = d;
                for (int i = 0; i < 4; i++)
                {
                    norms[v + i] = n;
                    uvs[v + i]   = uv;
                }
                tris[t + 0] = v + 0; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v + 0; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
                v += 4; t += 6;
            }

            AddFace(v100, v110, v111, v101, Vector3.right);    // +X
            AddFace(v001, v011, v010, v000, Vector3.left);     // -X
            AddFace(v010, v011, v111, v110, Vector3.up);       // +Y
            AddFace(v000, v100, v101, v001, Vector3.down);     // -Y
            AddFace(v001, v101, v111, v011, Vector3.forward);  // +Z
            AddFace(v000, v010, v110, v100, Vector3.back);     // -Z

            var mesh = new Mesh { name = "FillerCube" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Concatenate two meshes into one. Verts/normals/UVs append in
        // order, b's triangle indices get offset to point into the
        // merged vert array. Used for the "carved walls + interior
        // filler" solidify path. Doesn't attempt vertex welding: any
        // coincident verts are fine (the fragmenter de-dups cut-edge
        // intersections internally, and identical-position verts
        // belonging to disconnected surfaces is the desired state for
        // multi-component fragmentation).
        static Mesh MergeMeshes(Mesh a, Mesh b, string mergedName)
        {
            var aVerts = a.vertices;
            var aNorms = a.normals;
            var aUVs   = a.uv;
            var aTris  = a.triangles;
            var bVerts = b.vertices;
            var bNorms = b.normals;
            var bUVs   = b.uv;
            var bTris  = b.triangles;

            int total = aVerts.Length + bVerts.Length;
            var verts = new Vector3[total];
            var norms = new Vector3[total];
            var uvs   = new Vector2[total];

            // a-half. Pad short normal/UV arrays defensively — the
            // fragmenter's per-vertex arrays must be the same length
            // as the position array or its split-vertex interpolation
            // walks off the end.
            for (int i = 0; i < aVerts.Length; i++)
            {
                verts[i] = aVerts[i];
                norms[i] = i < aNorms.Length ? aNorms[i] : Vector3.up;
                uvs[i]   = i < aUVs.Length   ? aUVs[i]   : Vector2.zero;
            }

            int offset = aVerts.Length;
            for (int i = 0; i < bVerts.Length; i++)
            {
                verts[offset + i] = bVerts[i];
                norms[offset + i] = i < bNorms.Length ? bNorms[i] : Vector3.up;
                uvs[offset + i]   = i < bUVs.Length   ? bUVs[i]   : Vector2.zero;
            }

            var tris = new int[aTris.Length + bTris.Length];
            for (int i = 0; i < aTris.Length; i++) tris[i] = aTris[i];
            for (int i = 0; i < bTris.Length; i++)
                tris[aTris.Length + i] = bTris[i] + offset;

            var merged = new Mesh { name = mergedName };
            if (total > 65535)
                merged.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            merged.SetVertices(verts);
            merged.SetNormals(norms);
            merged.SetUVs(0, uvs);
            merged.SetTriangles(tris, 0);
            merged.RecalculateBounds();
            return merged;
        }

        // Mean of the source mesh's UV channel — used as the constant UV
        // for every vertex of the filler cube. For the kenney palette
        // atlas (each prefab uses a tight cluster of UVs around its
        // single material colour) this collapses to "the prefab's
        // dominant colour", which is what the filler should look like
        // so it blends with the carved wall fragments. Falls back to
        // (0.5, 0.5) for meshes with no UVs.
        static Vector2 ComputeAverageUV(Mesh source)
        {
            var uvs = source.uv;
            if (uvs == null || uvs.Length == 0) return new Vector2(0.5f, 0.5f);
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < uvs.Length; i++) sum += uvs[i];
            return sum / uvs.Length;
        }

        static FragmentResult[] BuildOffsetFragments(FragmentCache.CachedData cached, Vector3 worldOrigin)
        {
            var fragments = new FragmentResult[cached.Meshes.Length];
            Vector3 offset = worldOrigin - cached.MeshCenter;
            for (int i = 0; i < fragments.Length; i++)
            {
                fragments[i] = new FragmentResult
                {
                    Mesh = cached.Meshes[i],
                    Centroid = cached.LocalCentroids[i] + offset,
                };
            }
            return fragments;
        }

        void SetRenderersEnabled(bool enabled)
        {
            foreach (var r in _renderers) if (r != null) r.enabled = enabled;
        }

        void SetClickColliderEnabled(bool enabled)
        {
            // The click collider was attached by DemoBootstrap.AttachClickCollider —
            // a single BoxCollider sized to the target's local-space AABB.
            // Toggling enabled both removes it from raycasts (so the fractured
            // slot doesn't intercept taps) and from physics (so fragments
            // don't bounce off a now-invisible volume).
            var col = GetComponent<BoxCollider>();
            if (col != null) col.enabled = enabled;
        }

        int ResolveBakeKey(int count)
        {
            // gameObject InstanceID is unique per scene-instance — combined
            // with the fragment count, that's enough to keyspace bakes.
            return GetInstanceID() ^ (count * 7919);
        }
    }
}
