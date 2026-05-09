using UnityEngine;
using UnityEngine.Rendering;

namespace MeshFracture
{
    /// <summary>
    /// Per-fragment lifecycle: spawns one <see cref="GameObject"/> per cached
    /// fragment mesh, simulates ballistic motion + tumble (CPU sim by default,
    /// or Unity physics when <see cref="UseUnityPhysics"/> is enabled),
    /// drives a noise-modulated trail, smoothly fades the chunk out, and
    /// self-destructs at lifetime end. Fire-and-forget — call
    /// <see cref="Initialize"/> once and the component handles the rest.
    ///
    /// Two simulation modes:
    ///   - CPU (default): cheap ballistic + cheap-floor at y=-1. Zero scene
    ///     interaction, runs anywhere, no Rigidbody allocation.
    ///   - Unity physics (<see cref="UseUnityPhysics"/>=true): each fragment
    ///     gets a <see cref="Rigidbody"/> + convex <see cref="MeshCollider"/>
    ///     so it bounces off any collider in the scene (ground, pedestals,
    ///     walls) with proper restitution and friction.
    ///
    /// Two dissolve trigger modes:
    ///   - Time-based (default): dissolve fires <see cref="DissolveDuration"/>
    ///     seconds before <see cref="Lifetime"/> regardless of motion.
    ///   - Settle-based (<see cref="DissolveAfterSettle"/>=true): fragments
    ///     finish moving first, hold for <see cref="SettleHoldDuration"/>,
    ///     then dissolve. If motion never stops, the time-based path
    ///     fires as a fallback at <see cref="Lifetime"/> − <see cref="DissolveDuration"/>
    ///     — so the fade always plays before the burst ends.
    ///
    /// All fragments are children of this MonoBehaviour's GameObject, so
    /// destroying the parent disposes the whole burst at once. Defaults
    /// assume a Y-up world with gravity along -Y. For 2D / side-on games,
    /// set <see cref="LockToXY"/> (CPU sim only). For non-Y gravity, set
    /// <see cref="GravityVector"/> (CPU sim only — physics mode uses
    /// Unity's <c>Physics.gravity</c>).
    /// </summary>
    public class FractureBurst : MonoBehaviour
    {
        // Fragment simulation state. Kept separate from the GameObject array
        // so the inner loop doesn't need to call into Unity's transform API
        // every step — we only sync to transforms once per Update. (CPU sim only.)
        private struct FragmentState
        {
            public Vector3 position;
            public Vector3 velocity;
            public Quaternion rotation;
            public Vector3 angularVelocity;
            public bool settled;
        }

        // ─── Configurable per-burst ────────────────────────────────────────
        /// <summary>Target burst duration in seconds. The fade-out always starts by <see cref="Lifetime"/> − <see cref="DissolveDuration"/> at the latest — settle-based dissolve can fire earlier when motion stops, but the lifetime fallback guarantees the chunks fade rather than pop. A true safety net at 2× Lifetime catches the edge case where the fade itself stalls.</summary>
        public float Lifetime = 5f;
        /// <summary>Seconds the alpha fade-out takes (animates <c>_BaseColor.a</c> 1→0 on the exterior + interior materials).</summary>
        public float DissolveDuration = 1f;
        /// <summary>If true, dissolve waits until every fragment has been at rest for <see cref="SettleHoldDuration"/>. Use with <see cref="UseUnityPhysics"/> when you want fragments to stop, briefly settle, and then fade. False = legacy time-based dissolve at <see cref="Lifetime"/> − <see cref="DissolveDuration"/>.</summary>
        public bool DissolveAfterSettle = false;
        /// <summary>Seconds to hold once all fragments are at rest before starting the dissolve fade. Lets the eye register the settled state. Only used when <see cref="DissolveAfterSettle"/> is true.</summary>
        public float SettleHoldDuration = 1f;
        /// <summary>If true, each fragment gets a <see cref="Rigidbody"/> + convex <see cref="MeshCollider"/> and Unity physics handles motion + collision. Requires the scene to have colliders for fragments to bounce off (ground, pedestals, walls). False = CPU ballistic sim with cheap y=-1 floor.</summary>
        public bool UseUnityPhysics = false;
        /// <summary>Restitution coefficient applied to the per-fragment <see cref="PhysicsMaterial"/>. Range 0 (no bounce) to 1 (perfect bounce). Only used when <see cref="UseUnityPhysics"/> is true.</summary>
        public float Bounciness = 0.35f;
        /// <summary>Dynamic + static friction on the per-fragment physics material. Higher = fragments stop sliding faster. Only used when <see cref="UseUnityPhysics"/> is true.</summary>
        public float Friction = 0.5f;
        /// <summary>Per-fragment mass for Unity physics. Higher mass = more inertia / less effect from collisions. Only used when <see cref="UseUnityPhysics"/> is true.</summary>
        public float FragmentMass = 0.5f;
        /// <summary>World-space gravity vector applied per second (CPU sim only). Default = (0, -25, 0). Unity physics mode uses <c>Physics.gravity</c>.</summary>
        public Vector3 GravityVector = new Vector3(0f, -25f, 0f);
        /// <summary>If true, locks fragment motion + tumble to the XY plane (side-on 2D). CPU sim only.</summary>
        public bool LockToXY = false;
        /// <summary>
        /// Optional plane lock — when this vector is non-zero, every
        /// fragment's linear velocity is projected onto the plane
        /// perpendicular to this normal at burst time AND each CPU
        /// integration tick, and angular velocity is reduced to its
        /// component along this normal so chunks spin in the locked plane
        /// rather than tipping out of it. Generalises <see cref="LockToXY"/>
        /// (LockToXY=true is equivalent to LockPlaneNormal=Vector3.forward)
        /// for camera-facing sprite billboards: pass <c>Camera.main.transform.forward</c>
        /// at fracture time and fragments stay in the sprite's flat plane
        /// regardless of how the camera was oriented when the burst
        /// spawned. CPU sim only — Unity Rigidbody has no per-axis or
        /// per-plane constraint that maps cleanly onto an arbitrary
        /// world-space plane, so callers should also set
        /// <see cref="UseUnityPhysics"/>=false on sprite-mode bursts.
        /// </summary>
        public Vector3 LockPlaneNormal = Vector3.zero;
        /// <summary>Disable trail rendering by setting trail width to 0. Useful for performance-sensitive mobile builds.</summary>
        public bool EnableTrails = true;

        // ─── Internal ──────────────────────────────────────────────────────
        private GameObject[] fragmentObjects;
        private MeshRenderer[] fragmentRenderers;
        private Rigidbody[] fragmentRigidbodies;
        private TrailRenderer[] fragmentTrails;
        private MaterialPropertyBlock[] propBlocks;
        private FragmentState[] cpuStates;
        private int fragmentCount;

        private float spawnTime;
        private bool initialized;
        private bool dissolving;
        private float dissolveStartTime;
        private float allSettledStartTime;  // -1 when not currently settled
        private float baseTrailWidth;
        private float[] trailNoiseOffsets;
        private float[] trailNoiseFreqs;

        // Per-burst shared material instances. Captured in Initialize so the
        // fade pass in Update can drive `_BaseColor.a` directly on the
        // material — MaterialPropertyBlock-based overrides on URP/SRP-Batcher
        // shaders are not always honoured for properties declared inside
        // UnityPerMaterial CBUFFER (the override is silently dropped on some
        // GPU/driver/shader-keyword combos), and the fade goes invisible as
        // a result. Direct material mutation always reaches the shader.
        // Safe because the caller hands us instances unique to this burst
        // (DemoTarget creates fresh ones on every Fracture()).
        //
        // Materials are URP/Lit configured for transparent + Cull Off via
        // MaterialFactory.ConfigureFractureTransparent. URP/Lit's surface
        // alpha lives in `_BaseColor.a` when `_Surface == 1` (Transparent),
        // so the fade animates the alpha channel of the base color instead
        // of a separate `_Alpha` float.
        private Material sharedExteriorMaterial;
        private Material sharedInteriorMaterial;
        // Cache the burst's starting RGB values so the fade pass can write
        // `_BaseColor.a` without re-reading the color every frame and
        // without clobbering the source tint.
        private Color sharedExteriorBaseRGB = Color.white;
        private Color sharedInteriorBaseRGB = Color.white;

        // Progressive build state. Initialize captures inputs and pre-
        // computes per-fragment velocities/rotations, but defers GameObject
        // instantiation to Update where it builds BUILD_BATCH_SIZE fragments
        // per frame. Spawning all 8 fragments in one Initialize call costs
        // 50–180 ms per burst (Rigidbody + MeshCollider + TrailRenderer +
        // Material allocation × 8) and produces visible jitter on Fracture
        // All; spreading the same work across ~4 frames keeps each frame
        // under the 16 ms budget.
        private FragmentResult[] pendingFragments;
        private Quaternion       pendingMeshRotation;
        private Gradient         pendingTrailGradient;
        private float            pendingTrailWidth;
        private PhysicsMaterial  pendingPhysicsMat;
        private int              buildIndex;            // next fragment to instantiate
        private const int BUILD_BATCH_SIZE = 2;         // fragments built per Update tick

        /// <summary>Optional pre-baked convex collider meshes paired one-to-one with the fragments passed to <see cref="Initialize"/>. When set, the fragments use these meshes directly — and because the collision data is already cooked (see <see cref="FragmentCache.CachedData.ColliderMeshes"/>), the MeshCollider assignment skips Unity's convex-hull cook entirely. The single biggest contributor to per-fracture cost on dense source meshes; setting this field eliminates it.</summary>
        public Mesh[] PreBakedColliderMeshes;

        /// <summary>True once every pending fragment has been instantiated and the inter-fragment IgnoreCollision pass has run. Callers use this to serialize Fracture All — kicking off the next burst before the current one finishes building stacks per-frame work and reintroduces the jitter the progressive build was meant to fix.</summary>
        public bool IsFullyInitialized => initialized;

        // Settle thresholds — fragments below these speeds count as "at rest"
        // for dissolve-after-settle gating. Squared values so the inner check
        // skips a sqrt per frame per fragment.
        private const float SETTLE_LIN_SPEED_SQ = 0.04f;   // 0.2 units/sec
        private const float SETTLE_ANG_SPEED_SQ = 0.25f;   // 0.5 rad/sec

        // Max unique-position count we'll feed to a convex MeshCollider.
        // The convex hull of N points has at most 2N−4 triangles, and
        // Unity's MeshCollider.convex caps at 256 output triangles before
        // falling back to a partial hull. 100 keeps the worst-case hull
        // (196 tris) safely under the limit with margin for Unity's
        // numerical robustness pass.
        private const int COLLIDER_VERT_LIMIT = 100;

        /// <summary>
        /// Spawn the fragment GameObjects and start simulating. Call once per
        /// burst — the component is single-shot.
        /// </summary>
        /// <param name="fragments">Array of cells from <see cref="MeshFragmenter.Fragment"/> or pre-baked from <see cref="FragmentCache"/>. Centroids should already be in world space (apply your offset before calling).</param>
        /// <param name="exteriorMaterial">Material for the original surface (submesh 0). Should be configured for transparent rendering (URP/Lit with <c>_Surface = 1</c>) so the burst's <c>_BaseColor.a</c> fade-out animation is visible. <see cref="MeshFractureDemo.MaterialFactory.ExteriorFractureMaterial"/> sets this up.</param>
        /// <param name="interiorMaterial">Material for the cap faces (submesh 1) — fresh interior look. Typically a darker version of the exterior or a dedicated "raw" material.</param>
        /// <param name="explosionForce">Initial outward speed in units/sec applied to each fragment. Higher = wider spread; lower = "drops apart" feel.</param>
        /// <param name="trailWidth">Base trail width in world units. Trails noise-modulate around this value.</param>
        /// <param name="trailGradient">Color gradient applied to each fragment's trail. Pass <c>null</c> for default red.</param>
        /// <param name="meshRotation">Rotation to apply to each fragment, e.g. <c>FragmentCache.CachedData.MeshRotation</c>. Pass <see cref="Quaternion.identity"/> for no rotation.</param>
        public void Initialize(
            FragmentResult[] fragments,
            Material exteriorMaterial,
            Material interiorMaterial,
            float explosionForce = 18f,
            float trailWidth = 0.12f,
            Gradient trailGradient = null,
            Quaternion meshRotation = default)
        {
            spawnTime = Time.time;
            fragmentCount = fragments.Length;
            fragmentObjects = new GameObject[fragmentCount];
            fragmentRenderers = new MeshRenderer[fragmentCount];
            fragmentRigidbodies = new Rigidbody[fragmentCount];
            fragmentTrails = new TrailRenderer[fragmentCount];
            propBlocks = new MaterialPropertyBlock[fragmentCount];
            cpuStates = new FragmentState[fragmentCount];
            baseTrailWidth = trailWidth;
            trailNoiseOffsets = new float[fragmentCount];
            trailNoiseFreqs = new float[fragmentCount];
            allSettledStartTime = -1f;

            // Cache the burst's exterior + interior material instances so
            // the per-frame fade pass can drive `_BaseColor.a` on them
            // directly — see the field comment for why PropertyBlock-based
            // override wasn't reliable. Snapshot the starting RGB now so the
            // fade pass can write the alpha channel without round-tripping
            // GetColor every frame, and force alpha to 1 so the fragment
            // always starts fully visible regardless of any cached shader
            // state on the source material.
            sharedExteriorMaterial = exteriorMaterial;
            sharedInteriorMaterial = interiorMaterial;
            if (sharedExteriorMaterial != null && sharedExteriorMaterial.HasProperty("_BaseColor"))
            {
                var c = sharedExteriorMaterial.GetColor("_BaseColor");
                sharedExteriorBaseRGB = new Color(c.r, c.g, c.b, 1f);
                sharedExteriorMaterial.SetColor("_BaseColor", sharedExteriorBaseRGB);
            }
            if (sharedInteriorMaterial != null && sharedInteriorMaterial.HasProperty("_BaseColor"))
            {
                var c = sharedInteriorMaterial.GetColor("_BaseColor");
                sharedInteriorBaseRGB = new Color(c.r, c.g, c.b, 1f);
                sharedInteriorMaterial.SetColor("_BaseColor", sharedInteriorBaseRGB);
            }

            if (meshRotation == default(Quaternion) || (meshRotation.x == 0 && meshRotation.y == 0 && meshRotation.z == 0 && meshRotation.w == 0))
                meshRotation = Quaternion.identity;

            if (trailGradient == null)
            {
                trailGradient = new Gradient();
                trailGradient.SetKeys(
                    new[] { new GradientColorKey(new Color(0.6f, 0f, 0f), 0f), new GradientColorKey(new Color(0.3f, 0f, 0f), 1f) },
                    new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) }
                );
            }

            // Use the average of all fragment centroids as the explosion
            // origin — gives a deterministic "outward from middle" direction
            // for each fragment regardless of the source mesh's bounds.
            Vector3 explosionCenter = Vector3.zero;
            for (int i = 0; i < fragments.Length; i++) explosionCenter += fragments[i].Centroid;
            explosionCenter /= fragments.Length;

            // Shared physics material for all fragments in this burst (one
            // alloc per burst, not per fragment). Only built when physics
            // mode is on.
            PhysicsMaterial sharedPhys = null;
            if (UseUnityPhysics)
            {
                sharedPhys = new PhysicsMaterial("FragmentPhysics")
                {
                    bounciness       = Bounciness,
                    dynamicFriction  = Friction,
                    staticFriction   = Friction,
                    bounceCombine    = PhysicsMaterialCombine.Average,
                    frictionCombine  = PhysicsMaterialCombine.Average,
                };
            }

            // Pre-compute per-fragment initial velocity / angular velocity /
            // trail noise into cpuStates + the trail noise arrays. Doing this
            // up-front (not during the progressive Update build) means the
            // Random.* sequence is consumed deterministically in one place,
            // and so the build batches don't have to re-derive explosion-
            // center math each tick. The rest of the per-fragment work
            // (GameObject + components) gets deferred to Update.
            float upwardBias = UseUnityPhysics ? 0.1f : 0.4f;
            bool lockToPlane = LockPlaneNormal.sqrMagnitude > 1e-4f;
            Vector3 planeNormal = lockToPlane ? LockPlaneNormal.normalized : Vector3.zero;
            for (int i = 0; i < fragmentCount; i++)
            {
                var frag = fragments[i];

                Vector3 dir = (frag.Centroid - explosionCenter);
                if (lockToPlane) dir = Vector3.ProjectOnPlane(dir, planeNormal);
                else if (LockToXY) dir.z = 0f;
                if (dir.sqrMagnitude < 0.001f)
                {
                    if (lockToPlane)
                    {
                        // Fragment sits on the explosion center; pick a
                        // random in-plane direction so the burst still
                        // spreads outward instead of stalling.
                        Vector3 random3 = new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f));
                        dir = Vector3.ProjectOnPlane(random3, planeNormal);
                        if (dir.sqrMagnitude < 0.001f) dir = Vector3.up;  // pathological: random was parallel to normal
                    }
                    else
                    {
                        dir = LockToXY
                            ? new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), 0f)
                            : new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f));
                    }
                }
                dir = dir.normalized;

                Vector3 randomSpread = new Vector3(
                    Random.Range(-0.3f, 0.3f),
                    Random.Range(-0.3f, 0.3f),
                    Random.Range(-0.3f, 0.3f));
                if (lockToPlane) randomSpread = Vector3.ProjectOnPlane(randomSpread, planeNormal);
                else if (LockToXY) randomSpread.z = 0f;

                Vector3 velocity = (dir + randomSpread).normalized * explosionForce
                    + Vector3.up * (explosionForce * upwardBias);
                if (lockToPlane) velocity = Vector3.ProjectOnPlane(velocity, planeNormal);
                else if (LockToXY) velocity.z = 0f;

                Vector3 angVel;
                if (lockToPlane)
                {
                    // Spin around the plane normal so chunks rotate IN the
                    // locked plane (camera POV: 2D twirl) rather than
                    // tumbling out of it.
                    angVel = planeNormal * Random.Range(-10f, 10f);
                }
                else if (LockToXY)
                {
                    angVel = new Vector3(0f, 0f, Random.Range(-10f, 10f));
                }
                else
                {
                    angVel = new Vector3(Random.Range(-10f, 10f), Random.Range(-10f, 10f), Random.Range(-10f, 10f));
                }

                cpuStates[i] = new FragmentState
                {
                    position = frag.Centroid,
                    velocity = velocity,
                    rotation = meshRotation,
                    angularVelocity = angVel,
                    settled = false,
                };

                if (EnableTrails)
                {
                    trailNoiseOffsets[i] = Random.Range(0f, 100f);
                    trailNoiseFreqs[i]   = Random.Range(3f, 8f);
                }
            }

            // Stash the inputs that BuildOneFragment will need on subsequent
            // Update ticks. initialized stays false until the build loop in
            // Update finishes — Update's motion + dissolve passes early-out
            // until then.
            pendingFragments     = fragments;
            pendingMeshRotation  = meshRotation;
            pendingTrailGradient = trailGradient;
            pendingTrailWidth    = trailWidth;
            pendingPhysicsMat    = sharedPhys;
            buildIndex           = 0;
        }

        // Build one fragment — instantiate its GameObject, components, and
        // (if physics mode) Rigidbody + collider. Called once per fragment
        // by Update, in batches. Reads pre-computed state from cpuStates
        // and the trail-noise arrays so this stays focused on the costly
        // engine-side allocations, which is what the spreading is meant to
        // dilute.
        private void BuildOneFragment(int i)
        {
            var frag = pendingFragments[i];
            var go = new GameObject($"Frag_{i}");
            go.transform.SetParent(transform, false);
            go.transform.position = frag.Centroid;
            go.transform.rotation = pendingMeshRotation;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = frag.Mesh;

            var mr = go.AddComponent<MeshRenderer>();
            // submesh 0 = exterior, submesh 1 = interior (cap faces)
            mr.sharedMaterials = new[] { sharedExteriorMaterial, sharedInteriorMaterial };
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            if (EnableTrails)
            {
                var trail = go.AddComponent<TrailRenderer>();
                trail.time = 1.5f;
                trail.widthMultiplier = pendingTrailWidth;
                trail.widthCurve = BuildNoisyWidthCurve();
                trail.colorGradient = pendingTrailGradient;
                trail.material = BuildTrailMaterial();
                trail.generateLightingData = false;
                trail.autodestruct = false;
                trail.minVertexDistance = 0.05f;
                fragmentTrails[i] = trail;
            }

            fragmentObjects[i] = go;
            fragmentRenderers[i] = mr;
            propBlocks[i] = new MaterialPropertyBlock();

            if (UseUnityPhysics)
            {
                // Voronoi cells are convex polyhedra; use the pre-baked
                // collider mesh from the cache when available so Unity's
                // convex-hull cook (the largest single contributor to
                // per-fracture time on dense meshes) doesn't run on the
                // build frame. When PreBakedColliderMeshes is null we
                // fall back to building + cooking on demand here, which
                // is what the standalone (non-cache) usage paths get.
                Mesh collMesh = (PreBakedColliderMeshes != null
                                  && i < PreBakedColliderMeshes.Length
                                  && PreBakedColliderMeshes[i] != null)
                    ? PreBakedColliderMeshes[i]
                    : BuildColliderMesh(frag.Mesh);

                Collider col;
                if (collMesh.vertexCount <= COLLIDER_VERT_LIMIT)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = collMesh;
                    mc.convex = true;
                    col = mc;
                }
                else
                {
                    var bc = go.AddComponent<BoxCollider>();
                    var b = frag.Mesh.bounds;
                    bc.center = b.center;
                    bc.size   = b.size;
                    col = bc;
                }
                col.sharedMaterial = pendingPhysicsMat;

                var rb = go.AddComponent<Rigidbody>();
                rb.mass = FragmentMass;
                rb.linearDamping  = 0.05f;
                rb.angularDamping = 0.5f;
                rb.useGravity = true;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.linearVelocity  = cpuStates[i].velocity;
                rb.angularVelocity = cpuStates[i].angularVelocity;
                fragmentRigidbodies[i] = rb;
            }
        }

        // Final pass after every fragment has been instantiated. Pair-wise
        // IgnoreCollision can't run incrementally — peer colliders for
        // pair (a, b) need both to exist before the call — so it lives
        // here, fired once when buildIndex hits fragmentCount.
        private void FinalizeBuild()
        {
            if (UseUnityPhysics)
            {
                // Disable inter-fragment collision. Voronoi cells share
                // boundary surfaces with their neighbours, so at spawn every
                // pair of adjacent fragment colliders is exactly touching on
                // their shared cut plane. Unity's contact solver applies
                // depenetration impulses along the surface normal — and for
                // a top-vs-bottom pair the normal is straight up, so the
                // upper fragment launches skyward on top of its authored
                // explosion velocity. Pair-wise IgnoreCollision is cheaper
                // than a layer shuffle and self-cleans when the burst is
                // destroyed. Fragments still collide with all non-fragment
                // scene geometry: ground, pedestals, the showcase wall.
                for (int a = 0; a < fragmentCount; a++)
                {
                    if (fragmentObjects[a] == null) continue;
                    var ca = fragmentObjects[a].GetComponent<Collider>();
                    if (ca == null) continue;
                    for (int b = a + 1; b < fragmentCount; b++)
                    {
                        if (fragmentObjects[b] == null) continue;
                        var cb = fragmentObjects[b].GetComponent<Collider>();
                        if (cb == null) continue;
                        Physics.IgnoreCollision(ca, cb, true);
                    }
                }
            }
            initialized = true;
        }

        private void Update()
        {
            // Progressive build: instantiate BUILD_BATCH_SIZE fragments per
            // frame until all are in place. Once the last one lands the
            // pair-wise IgnoreCollision pass runs and initialized flips
            // true. This pulls the previously-monolithic Initialize cost
            // (~50–180 ms for an 8-fragment burst with Rigidbodies +
            // MeshColliders + TrailRenderers) into bite-sized per-frame
            // chunks under the 16 ms budget.
            if (pendingFragments != null && buildIndex < fragmentCount)
            {
                int end = Mathf.Min(buildIndex + BUILD_BATCH_SIZE, fragmentCount);
                for (int i = buildIndex; i < end; i++) BuildOneFragment(i);
                buildIndex = end;
                if (buildIndex >= fragmentCount) FinalizeBuild();
                // Skip the rest of Update this frame — motion + dissolve
                // can't run until every fragment exists. spawnTime was set
                // back in Initialize so the dissolve clock starts when the
                // burst was *requested*, not when it finishes building;
                // this matters because the build itself takes a few frames
                // and the user expects the burst to feel "instant".
                return;
            }

            if (!initialized) return;

            float elapsed = Time.time - spawnTime;

            // True safety net: 2× Lifetime — only fires if the dissolve
            // itself somehow stalls (force-start branch below already
            // guarantees the fade kicks in by Lifetime − DissolveDuration,
            // so this should never actually fire). Kept defensively so a
            // never-destroyed burst can't leak forever.
            if (elapsed >= Lifetime * 2f)
            {
                Destroy(gameObject);
                return;
            }

            float dt = Time.deltaTime;

            if (!UseUnityPhysics)
            {
                SimulateCPU(dt);
                ApplyStates();
            }
            // else: Rigidbody.linearVelocity drives transform via Unity's
            // physics step; we just read state in the dissolve trigger below.

            // Decide whether to start the dissolve. Two paths can fire it:
            //   1. Settle-based — all fragments came to rest, held still
            //      for SettleHoldDuration. The "ideal" path: dissolve
            //      starts as soon as motion stops.
            //   2. Lifetime fallback — DissolveDuration before Lifetime,
            //      regardless of motion. Catches fragments that bounce
            //      forever, slide off the stage, or otherwise never trip
            //      the settle thresholds. Without this fallback, the
            //      burst was hard-destroyed at Lifetime with zero fade,
            //      visible to users as "fragments pop out instantly".
            // Whichever fires first wins.
            if (!dissolving)
            {
                bool settleReady = false;
                if (DissolveAfterSettle)
                {
                    // Track the moment all fragments first appeared at rest.
                    // If anything wakes back up (collision restart, sliding),
                    // reset the clock — we want a contiguous SettleHoldDuration
                    // of stillness, not cumulative.
                    bool allSettled = AreAllAtRest();
                    if (allSettled)
                    {
                        if (allSettledStartTime < 0f) allSettledStartTime = elapsed;
                    }
                    else
                    {
                        allSettledStartTime = -1f;
                    }
                    settleReady = allSettledStartTime >= 0f
                                  && (elapsed - allSettledStartTime) >= SettleHoldDuration;
                }

                bool timeReady = elapsed >= (Lifetime - DissolveDuration);

                if (settleReady || timeReady)
                {
                    dissolving = true;
                    dissolveStartTime = elapsed;
                }
            }

            // Fade-out: alpha animates from 1 → 0 over DissolveDuration once
            // the dissolve trigger fires. Drives URP/Lit's `_BaseColor.a`
            // property on the burst's shared materials directly. Both
            // submesh materials (exterior + interior) are mutated so the
            // chunk fades uniformly. We don't go through MaterialProperty-
            // Blocks here because URP's SRP-Batcher path with this shader
            // sometimes drops the override (the "objects disappear
            // instantly when time's up" symptom — fade alpha never
            // reached the GPU), and direct mutation is safe since each
            // burst owns its material instances.
            float fadeT = 0f;
            if (dissolving)
            {
                fadeT = Mathf.Clamp01((elapsed - dissolveStartTime) / DissolveDuration);
                float alpha = 1f - fadeT;
                if (sharedExteriorMaterial != null && sharedExteriorMaterial.HasProperty("_BaseColor"))
                {
                    var c = sharedExteriorBaseRGB; c.a = alpha;
                    sharedExteriorMaterial.SetColor("_BaseColor", c);
                }
                if (sharedInteriorMaterial != null && sharedInteriorMaterial.HasProperty("_BaseColor"))
                {
                    var c = sharedInteriorBaseRGB; c.a = alpha;
                    sharedInteriorMaterial.SetColor("_BaseColor", c);
                }
                if (fadeT >= 1f)
                {
                    Destroy(gameObject);
                    return;
                }
            }

            if (EnableTrails)
            {
                float trailFade = dissolving ? (1f - fadeT) : 1f;
                for (int i = 0; i < fragmentCount; i++)
                {
                    if (fragmentTrails[i] == null) continue;
                    float noise = Mathf.PerlinNoise(elapsed * trailNoiseFreqs[i] + trailNoiseOffsets[i], i * 0.7f);
                    float noiseMultiplier = Mathf.Lerp(0.3f, 1.7f, noise);
                    fragmentTrails[i].widthMultiplier = baseTrailWidth * noiseMultiplier * trailFade;
                }
            }
        }

        // True if every still-existing fragment is below the linear AND
        // angular speed thresholds. Pure read — no state mutation.
        private bool AreAllAtRest()
        {
            for (int i = 0; i < fragmentCount; i++)
            {
                if (UseUnityPhysics)
                {
                    var rb = fragmentRigidbodies[i];
                    if (rb == null) continue;
                    if (rb.linearVelocity.sqrMagnitude  > SETTLE_LIN_SPEED_SQ) return false;
                    if (rb.angularVelocity.sqrMagnitude > SETTLE_ANG_SPEED_SQ) return false;
                }
                else
                {
                    if (!cpuStates[i].settled) return false;
                }
            }
            return true;
        }

        private void SimulateCPU(float dt)
        {
            bool lockToPlane = LockPlaneNormal.sqrMagnitude > 1e-4f;
            Vector3 planeNormal = lockToPlane ? LockPlaneNormal.normalized : Vector3.zero;
            for (int i = 0; i < fragmentCount; i++)
            {
                ref var s = ref cpuStates[i];
                if (s.settled) continue;

                s.velocity += GravityVector * dt;
                if (lockToPlane) s.velocity = Vector3.ProjectOnPlane(s.velocity, planeNormal);
                else if (LockToXY) s.velocity.z = 0f;

                Vector3 newPos = s.position + s.velocity * dt;
                if (lockToPlane)
                {
                    // Keep the fragment on the same plane it spawned on — drift
                    // along the normal would otherwise compound under gravity
                    // for non-vertical lock planes (camera looking up / down).
                    Vector3 fromStart = newPos - s.position;
                    fromStart = Vector3.ProjectOnPlane(fromStart, planeNormal);
                    newPos = s.position + fromStart;
                }
                else if (LockToXY) newPos.z = s.position.z;

                // Cheap floor at y = -1. For collision against actual world
                // geometry, set UseUnityPhysics=true — that swaps the whole
                // motion path to Rigidbody+MeshCollider against scene colliders.
                if (newPos.y < -1f)
                {
                    newPos.y = -1f;
                    s.velocity.y *= -0.3f;
                    s.velocity.x *= 0.8f;
                    s.velocity.z *= 0.8f;
                    if (s.velocity.sqrMagnitude < 0.25f)
                        s.settled = true;
                }

                s.position = newPos;

                // Quaternion derivative integration. Faster than
                // Quaternion.Euler(angVel * dt) at small step sizes and
                // doesn't suffer from gimbal-lock artefacts when the
                // angular velocity is near a singular direction.
                if (lockToPlane)
                {
                    // Project angular velocity onto plane normal — chunks
                    // spin in the locked plane only, no out-of-plane wobble.
                    s.angularVelocity = planeNormal * Vector3.Dot(s.angularVelocity, planeNormal);
                }
                else if (LockToXY)
                {
                    s.angularVelocity.x = 0f;
                    s.angularVelocity.y = 0f;
                }
                Vector3 halfOmega = s.angularVelocity * dt * 0.5f;
                Quaternion dq = new Quaternion(halfOmega.x, halfOmega.y, halfOmega.z, 0f) * s.rotation;
                s.rotation = NormalizeQuat(new Quaternion(
                    s.rotation.x + dq.x,
                    s.rotation.y + dq.y,
                    s.rotation.z + dq.z,
                    s.rotation.w + dq.w));

                s.angularVelocity *= (1f - 3f * dt);
            }
        }

        private void ApplyStates()
        {
            for (int i = 0; i < fragmentCount; i++)
            {
                if (fragmentObjects[i] == null) continue;
                var s = cpuStates[i];
                fragmentObjects[i].transform.position = s.position;
                fragmentObjects[i].transform.rotation = s.rotation;
            }
        }

        private static Quaternion NormalizeQuat(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 0.0001f) return Quaternion.identity;
            float inv = 1f / mag;
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }

        // Noisy width curve: tapered envelope multiplied by per-key random
        // jitter. Linear tangents preserve the bumps; smooth tangents would
        // sand them down to invisible gentle waves. Result: trails read as
        // "spurts" rather than "smooth ribbons" — feels more visceral.
        private static AnimationCurve BuildNoisyWidthCurve()
        {
            const int keyCount = 12;
            var keys = new Keyframe[keyCount];
            for (int k = 0; k < keyCount; k++)
            {
                float t = (float)k / (keyCount - 1);
                float envelope = 1f - t;
                keys[k] = new Keyframe(t, envelope * Random.Range(0.15f, 1f));
            }
            keys[0].value = 1f;
            keys[keyCount - 1].value = 0f;
            for (int k = 0; k < keyCount; k++)
            {
                float prev = k > 0 ? keys[k - 1].value : keys[0].value;
                float next = k < keyCount - 1 ? keys[k + 1].value : keys[k].value;
                float prevT = k > 0 ? keys[k - 1].time : keys[0].time;
                float nextT = k < keyCount - 1 ? keys[k + 1].time : keys[k].time;
                keys[k].inTangent = (k > 0) ? (keys[k].value - prev) / (keys[k].time - prevT) : 0f;
                keys[k].outTangent = (k < keyCount - 1) ? (next - keys[k].value) / (nextT - keys[k].time) : 0f;
            }
            return new AnimationCurve(keys);
        }

        // Position-dedup the fragment's vertex array into a separate Mesh
        // for use as a convex MeshCollider source. The visual mesh
        // duplicates polygon verts on each cap (so the cut face carries a
        // flat normal independent of the surrounding surface normals),
        // which doubles the input to Unity's convex-hull computation
        // without contributing any unique geometry. Stripping the
        // duplicates roughly halves the vert count and brings the hull
        // tri count back under Unity's 256-poly ceiling on every
        // fragment we generate from cube / sphere / kenney-prop sources.
        // For very dense source meshes the result may STILL exceed
        // COLLIDER_VERT_LIMIT — caller falls back to a BoxCollider in
        // that case rather than letting Unity emit a partial hull.
        private static Mesh BuildColliderMesh(Mesh source)
        {
            var srcVerts = source.vertices;
            if (srcVerts.Length == 0) return source;

            const float epsSq = 1e-8f;
            var unique = new System.Collections.Generic.List<Vector3>(srcVerts.Length / 2 + 4);
            var indexMap = new int[srcVerts.Length];

            for (int i = 0; i < srcVerts.Length; i++)
            {
                Vector3 p = srcVerts[i];
                int found = -1;
                // Linear scan — fragment vert counts are small (typically
                // < 200), so a hash-by-position would be more bookkeeping
                // than work saved.
                for (int j = 0; j < unique.Count; j++)
                {
                    if ((unique[j] - p).sqrMagnitude < epsSq) { found = j; break; }
                }
                if (found >= 0)
                {
                    indexMap[i] = found;
                }
                else
                {
                    indexMap[i] = unique.Count;
                    unique.Add(p);
                }
            }

            // No coincident verts on this mesh — convex hull would be
            // computed identically. Reuse the source instance to avoid an
            // unnecessary mesh allocation.
            if (unique.Count == srcVerts.Length) return source;

            var srcTris = source.triangles;  // both submeshes flattened
            var newTris = new int[srcTris.Length];
            for (int i = 0; i < srcTris.Length; i++) newTris[i] = indexMap[srcTris[i]];

            var collMesh = new Mesh { name = source.name + "_Coll" };
            if (unique.Count > 65535)
                collMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            collMesh.SetVertices(unique);
            collMesh.SetTriangles(newTris, 0);
            return collMesh;
        }

        // Builds an alpha-blended URP/Unlit material at runtime so users
        // don't need to ship a separate "trail" material asset. Falls back
        // to Sprites/Default when URP isn't present.
        private static Material BuildTrailMaterial()
        {
            var trailMat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
            trailMat.SetFloat("_Surface", 1f); // transparent
            trailMat.SetFloat("_Blend", 0f);   // alpha
            trailMat.SetFloat("_DstBlend", 10f); // OneMinusSrcAlpha
            trailMat.SetFloat("_SrcBlend", 5f);  // SrcAlpha
            trailMat.SetFloat("_ZWrite", 0f);
            trailMat.renderQueue = 3000;
            return trailMat;
        }
    }
}
