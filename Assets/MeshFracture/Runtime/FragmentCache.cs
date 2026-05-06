using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MeshFracture
{
    /// <summary>
    /// Pre-bakes Voronoi mesh fragments off the hot path. Voronoi
    /// fragmentation cost is O(n²) in fragment count and runs entirely on the
    /// CPU — at 8 fragments per character, profiling shows 3–9 ms spikes on
    /// desktop and proportionally worse on mobile. Doing the work at
    /// game-start (or first-load time) instead of at the moment of impact
    /// turns destruction into a cheap mesh swap.
    ///
    /// Pattern: queue once per <c>(modelId, fragmentCount)</c> pair via
    /// <see cref="RequestPreBake"/>. The worker reads the prefab's bind-pose
    /// <c>sharedMesh</c> immediately, then defers the Voronoi step to the
    /// next frame so the visual creation frame stays light. Subsequent calls
    /// for the same key are no-ops. Look up at impact time with
    /// <see cref="TryGet"/>; if the bake hasn't completed yet, fall back to
    /// your non-fragment effect (smoke puff, particle burst) — the visual is
    /// usually invisible by the next frame.
    /// </summary>
    public static class FragmentCache
    {
        public struct CachedData
        {
            /// <summary>Fragment meshes (shared, treat as read-only).</summary>
            public Mesh[] Meshes;
            /// <summary>Local-space centroids relative to <see cref="MeshCenter"/>.</summary>
            public Vector3[] LocalCentroids;
            /// <summary>Centroid of the source mesh's bounds. Subtract from a world-space death position to get the offset to apply to every centroid.</summary>
            public Vector3 MeshCenter;
            /// <summary>Rotation the source SkinnedMeshRenderer was authored with — apply when spawning fragments so they inherit the model's facing.</summary>
            public Quaternion MeshRotation;
            /// <summary>Position-deduped collider meshes paired one-to-one with <see cref="Meshes"/>, with their convex-hull collision data already cooked via <see cref="UnityEngine.Physics.BakeMesh"/>. When a consumer assigns one of these to <c>MeshCollider.sharedMesh</c> with <c>convex = true</c>, Unity reuses the cached PhysX shape instead of running the cook on the spawn frame — that's the difference between a 5–10 ms hitch per fragment at fracture time and zero. Always non-null when produced by <see cref="RequestPreBake"/> or <see cref="BakeSynchronous"/>; consumers checking length should still null-guard for safety.</summary>
            public Mesh[] ColliderMeshes;
        }

        private static readonly Dictionary<int, CachedData> s_cache = new();
        private static readonly HashSet<int> s_pending = new();
        private static FragmentCacheWorker s_worker;

        /// <summary>True if the cache has a finished bake for this key.</summary>
        public static bool TryGet(int key, out CachedData data)
            => s_cache.TryGetValue(key, out data);

        /// <summary>
        /// Queue an async bake for the given prefab. Reads the source
        /// <see cref="SkinnedMeshRenderer"/> or <see cref="MeshFilter"/>
        /// sharedMesh immediately (no instantiation), then runs Voronoi
        /// fragmentation on a coroutine — one bake per frame to avoid stacking
        /// spikes when several keys are requested at once. Safe to call multiple
        /// times for the same key; duplicates are ignored.
        /// </summary>
        /// <param name="key">Caller-defined identity for this bake. Typically a hash of (modelId, skinId, fragmentCount).</param>
        /// <param name="modelPrefab">The prefab to read the source mesh from. Reads the first <see cref="SkinnedMeshRenderer"/> child, then falls back to the first <see cref="MeshFilter"/>.</param>
        /// <param name="fragmentCount">Number of Voronoi cells to produce. 3 reads as "split in two-or-three" — usable on mobile. 8 reads as "shattered" — desktop default.</param>
        public static void RequestPreBake(int key, GameObject modelPrefab, int fragmentCount)
        {
            if (s_cache.ContainsKey(key) || s_pending.Contains(key))
                return;
            s_pending.Add(key);

            // Read source mesh from the prefab without instantiating it.
            // lossyScale captures FBX import scale baked into the hierarchy
            // (e.g. a model imported at scale 40) so the fragments end up
            // matching the in-scene character size, not the bind-pose size.
            Mesh sourceMesh = null;
            Quaternion meshRotation = Quaternion.identity;
            Vector3 modelScale = Vector3.one;

            var smr = modelPrefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null && smr.sharedMesh != null)
            {
                sourceMesh = Object.Instantiate(smr.sharedMesh);
                meshRotation = smr.transform.rotation;
                modelScale = smr.transform.lossyScale;
            }
            else
            {
                var mf = modelPrefab.GetComponentInChildren<MeshFilter>(true);
                if (mf != null && mf.sharedMesh != null)
                {
                    sourceMesh = Object.Instantiate(mf.sharedMesh);
                    meshRotation = mf.transform.rotation;
                    modelScale = mf.transform.lossyScale;
                }
            }

            if (sourceMesh != null && modelScale != Vector3.one)
            {
                var verts = sourceMesh.vertices;
                for (int i = 0; i < verts.Length; i++)
                    verts[i] = Vector3.Scale(verts[i], modelScale);
                sourceMesh.vertices = verts;
                sourceMesh.RecalculateBounds();
            }

            if (sourceMesh == null || sourceMesh.vertexCount == 0)
            {
                s_pending.Remove(key);
                return;
            }

            if (s_worker == null)
            {
                var go = new GameObject("[FragmentCacheWorker]");
                Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                s_worker = go.AddComponent<FragmentCacheWorker>();
            }

            s_worker.Enqueue(key, sourceMesh, meshRotation, fragmentCount);
        }

        /// <summary>
        /// Bake synchronously from an arbitrary mesh (no prefab required).
        /// Use this when the source is procedurally generated or already in
        /// memory, e.g. a runtime-baked <see cref="SkinnedMeshRenderer"/>
        /// pose. Blocks the main thread for the full Voronoi cost — call from
        /// a loading screen, not from gameplay code.
        /// </summary>
        public static void BakeSynchronous(int key, Mesh sourceMesh, Quaternion meshRotation, int fragmentCount)
        {
            if (s_cache.ContainsKey(key) || sourceMesh == null) return;

            Vector3 center = sourceMesh.bounds.center;
            var fragments = MeshFragmenter.Fragment(sourceMesh, fragmentCount, center);

            var data = new CachedData
            {
                Meshes = new Mesh[fragments.Length],
                LocalCentroids = new Vector3[fragments.Length],
                ColliderMeshes = new Mesh[fragments.Length],
                MeshCenter = center,
                MeshRotation = meshRotation,
            };
            for (int i = 0; i < fragments.Length; i++)
            {
                data.Meshes[i] = fragments[i].Mesh;
                data.LocalCentroids[i] = fragments[i].Centroid;
                data.ColliderMeshes[i] = BuildAndBakeColliderMesh(fragments[i].Mesh);
            }
            s_cache[key] = data;
            s_pending.Remove(key);
        }

        /// <summary>
        /// Position-dedups the fragment's vertex array into a separate Mesh
        /// suitable for use as a convex MeshCollider source, then runs
        /// <see cref="UnityEngine.Physics.BakeMesh"/> with <c>convex = true</c>
        /// so the cooked PhysX hull is cached on the mesh. When a consumer
        /// later assigns this mesh to a MeshCollider Unity reuses that cooked
        /// shape — zero hitch at spawn time.
        ///
        /// The dedup step matters because the visual fragment mesh duplicates
        /// polygon vertices on each cap (so the cut face carries a flat
        /// normal independent of the surrounding surface normals) and that
        /// doubles the input to Unity's convex-hull algorithm, often pushing
        /// it past the 256-poly ceiling. Stripping the duplicates roughly
        /// halves the vert count and keeps the hull tidy. Logic mirrors
        /// <see cref="FractureBurst"/>'s own internal helper but lives here
        /// so the cache can pre-bake without a forward dependency.
        /// </summary>
        public static Mesh BuildAndBakeColliderMesh(Mesh source)
        {
            var srcVerts = source.vertices;
            Mesh result;
            if (srcVerts.Length == 0)
            {
                result = source;
            }
            else
            {
                const float epsSq = 1e-8f;
                var unique = new List<Vector3>(srcVerts.Length / 2 + 4);
                var indexMap = new int[srcVerts.Length];
                for (int i = 0; i < srcVerts.Length; i++)
                {
                    Vector3 p = srcVerts[i];
                    int found = -1;
                    for (int j = 0; j < unique.Count; j++)
                    {
                        if ((unique[j] - p).sqrMagnitude < epsSq) { found = j; break; }
                    }
                    if (found >= 0) indexMap[i] = found;
                    else
                    {
                        indexMap[i] = unique.Count;
                        unique.Add(p);
                    }
                }
                if (unique.Count == srcVerts.Length)
                {
                    result = source;
                }
                else
                {
                    var srcTris = source.triangles;
                    var newTris = new int[srcTris.Length];
                    for (int i = 0; i < srcTris.Length; i++) newTris[i] = indexMap[srcTris[i]];
                    result = new Mesh { name = source.name + "_Coll" };
                    if (unique.Count > 65535)
                        result.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    result.SetVertices(unique);
                    result.SetTriangles(newTris, 0);
                }
            }
            // Cook the convex hull collision data NOW, on the pre-bake worker
            // frame — but only for fragments whose deduped vertex count
            // stays under the convex-MeshCollider tier threshold. Unity's
            // convex-hull algorithm caps at 256 output polygons, and an
            // N-vertex point set produces up to 2N−4 hull triangles, so
            // anything past N≈130 trips the "Couldn't create a Convex Mesh
            // ... 256 polygon limit, partial hull will be used" warning.
            // FractureBurst's runtime tier picks BoxCollider for those
            // dense fragments anyway, so the cook would just waste a
            // worker frame on PhysX data nobody reads. The threshold is
            // duplicated as a const here so FragmentCache doesn't take a
            // forward dependency on FractureBurst's internals.
            //
            // Unity 6 also replaced the int-instanceID overload of
            // BakeMesh with an EntityId overload; GetEntityId() avoids
            // the CS0618 deprecation warning.
            const int BAKE_VERT_LIMIT = 100;
            if (result.vertexCount <= BAKE_VERT_LIMIT)
                UnityEngine.Physics.BakeMesh(result.GetEntityId(), convex: true);
            return result;
        }

        internal static void StoreResult(int key, CachedData data)
        {
            s_cache[key] = data;
            s_pending.Remove(key);
        }

        /// <summary>Drop every cached bake. Use on scene unload or quality-setting changes.</summary>
        public static void Clear()
        {
            s_cache.Clear();
            s_pending.Clear();
        }

        /// <summary>Drop a single cached bake. Use when the underlying prefab/skin changes.</summary>
        public static void Evict(int key)
        {
            s_cache.Remove(key);
            s_pending.Remove(key);
        }
    }

    /// <summary>
    /// Coroutine driver that processes <see cref="FragmentCache.RequestPreBake"/>
    /// requests at one bake per frame. Internal — the cache spawns this on first
    /// use and parents it under <c>DontDestroyOnLoad</c>.
    /// </summary>
    internal class FragmentCacheWorker : MonoBehaviour
    {
        private struct Request
        {
            public int Key;
            public Mesh SourceMesh;
            public Quaternion MeshRotation;
            public int FragmentCount;
        }

        private readonly Queue<Request> queue = new();
        private bool processing;

        public void Enqueue(int key, Mesh sourceMesh, Quaternion meshRotation, int fragmentCount)
        {
            queue.Enqueue(new Request
            {
                Key = key,
                SourceMesh = sourceMesh,
                MeshRotation = meshRotation,
                FragmentCount = fragmentCount,
            });

            if (!processing)
                StartCoroutine(ProcessQueue());
        }

        private IEnumerator ProcessQueue()
        {
            processing = true;
            while (queue.Count > 0)
            {
                var req = queue.Dequeue();

                // Defer fragmentation to the NEXT frame so the visual
                // creation frame stays light. Without this yield, calling
                // RequestPreBake() during scene load makes the load frame
                // pay the Voronoi cost on top of mesh import + texture
                // upload + animator wiring — visible as a long startup hitch.
                yield return null;

                Vector3 center = req.SourceMesh.bounds.center;
                var fragments = MeshFragmenter.Fragment(req.SourceMesh, req.FragmentCount, center);

                var data = new FragmentCache.CachedData
                {
                    Meshes = new Mesh[fragments.Length],
                    LocalCentroids = new Vector3[fragments.Length],
                    ColliderMeshes = new Mesh[fragments.Length],
                    MeshCenter = center,
                    MeshRotation = req.MeshRotation,
                };

                for (int i = 0; i < fragments.Length; i++)
                {
                    data.Meshes[i] = fragments[i].Mesh;
                    data.LocalCentroids[i] = fragments[i].Centroid;
                    // Pre-bake the convex hull collision data on this idle
                    // pre-bake frame instead of paying for it during the
                    // fracture spawn frame. The cooked PhysX shape lives on
                    // the mesh asset; consumers reuse it when assigning to
                    // MeshCollider.sharedMesh.
                    data.ColliderMeshes[i] = FragmentCache.BuildAndBakeColliderMesh(fragments[i].Mesh);
                }

                FragmentCache.StoreResult(req.Key, data);

                // Yield between items so multiple pre-bakes don't stack
                // into one frame even when the queue has built up.
                if (queue.Count > 0)
                    yield return null;
            }
            processing = false;
        }
    }
}
