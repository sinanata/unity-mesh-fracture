# Unity Mesh Fracture

Drop-in **Voronoi mesh fracturing for Unity URP**. Pure-C# fragmentation, watertight fragments with separate exterior + cap submeshes, optional Unity-physics bouncing, alpha-fade dissolve — and a pre-bake cache (with cooked convex hulls) so the expensive Voronoi step happens at load time, not at the moment of impact.

<blockquote>
<a href="https://store.steampowered.com/app/2269500/"><img src="docs/leap-of-legends-icon.png" align="left" width="70" height="70" alt="Leap of Legends"></a>
Built for and battle-tested in <strong><a href="https://leapoflegends.com">Leap of Legends</a></strong> — a cross-platform multiplayer game in active development on Steam, Google Play (internal testing), TestFlight, and macOS. Every character that explodes in the game uses this fracturing pipeline. <a href="https://store.steampowered.com/app/2269500/">Wishlist on Steam</a> — public mobile store pages coming soon.
</blockquote>

---

```
MeshFragmenter.Fragment(mesh, count, center) →  FragmentResult[]   pure C#, zero deps
FragmentCache.RequestPreBake(key, prefab, n)  →  async load-time bake (+ cooked PhysX hulls)
FragmentCache.BakeSynchronous(...)            →  sync bake for loading-screen use
FragmentCache.TryGet(key, out cached)         →  O(1) impact-time lookup
new FractureBurst().Initialize(fragments,…)   →  ballistic + tumble + trail + alpha fade
burst.UseUnityPhysics = true                  →  optional Rigidbody + convex MeshCollider mode
```

## Why this exists

Mesh fracturing in Unity is a long-tail problem. Asset-store solutions are expensive and over-featured (rigid bodies, joints, multi-stage cracking). What most games actually want is one beat: **character/object explodes into a handful of fragments, fragments fall under gravity, fragments fade and disappear**. Cheap CPU sim by default; opt-in Unity physics when you need fragments to actually bounce off the world. No multi-stage cracking, no joints, no asset-store-style overhead — just convincing chunks.

This repo is that beat, finished:

- **Pure-C# Voronoi fragmenter.** No native libraries, no editor-only restrictions. Runs at edit time, build time, or runtime — your call. Watertight cells: caps from earlier clip planes get re-clipped + re-tagged by later planes, so even concave / hollow source meshes seal cleanly around the convex intersection.
- **Pre-bake cache.** Voronoi is O(n²) in fragment count and runs entirely on the CPU. Doing the work on a loading screen turns destruction into a cheap mesh swap. The cache also pre-cooks each fragment's convex `Physics.BakeMesh` hull on the worker frame so the runtime `MeshCollider` assignment skips Unity's hull-cook entirely.
- **Two-submesh fragments.** Submesh 0 = original surface (keep your character's textured skin). Submesh 1 = cap faces from the cuts (gets a separate "raw interior" material — bone, gore, fresh stone, whatever fits your art direction).
- **Two simulation modes.** Default: CPU ballistic sim with a cheap `y = -1` floor — zero scene interaction, runs anywhere. `UseUnityPhysics = true`: each fragment gets a `Rigidbody` + convex `MeshCollider` and bounces off whatever colliders the scene has (ground, pedestals, walls). Pair-wise `IgnoreCollision` between siblings stops the depenetration impulse that would otherwise launch shared-boundary fragments skyward at spawn.
- **Two dissolve trigger modes.** Default time-based: alpha fades over `DissolveDuration` ending at `Lifetime`. Settle-based (`DissolveAfterSettle = true`): fragments come to rest, hold for `SettleHoldDuration`, then fade. If motion never stops, the time-based fallback always fires so chunks fade rather than pop.
- **No custom shader.** Fragments use stock URP/Lit configured at runtime for transparent + Cull Off + ZWrite-on (`MaterialFactory.ConfigureFractureTransparent` — 10 lines, replicates what URP/Lit's editor inspector calls when you flip the Surface dropdown). Fade animates `_BaseColor.a` 1 → 0. Zero `.shader` assets to ship; one Unity validates on every release.
- **Progressive build.** `Initialize` captures inputs and pre-computes per-fragment state, then spawns `BUILD_BATCH_SIZE` fragments per `Update` tick instead of all at once — turns a 50–180 ms `Initialize` spike into bite-sized per-frame chunks under the 16 ms budget.

What you don't get: multi-stage cracking, sub-fragmentation, persistent debris. If you need those, look at Obi or RayFire. If you need "boom, debris, gone" with optional bounce physics, low per-impact GC, and a clean URP look, you're in the right place.

## Demo

**[▶ Live WebGL preview](https://sinanata.github.io/unity-mesh-fracture/)** — eight pedestals, click any object to fracture it, drag right-mouse to orbit, slider to retune fragment count.

The repo is a complete Unity project — clone, open in Unity 6, press Play. The demo scene auto-spawns:

- 2 primitives (cube, sphere) and 5 [Kenney pirate-kit](https://kenney.nl/assets/pirate-kit) props (barrel, crate, chest, cannon, rocks) for the static-mesh path.
- 1 [Kenney character](https://kenney.nl/assets/animated-characters-1) for the **`SkinnedMeshRenderer.BakeMesh`** path — fragments match whatever pose the animator was in at the moment of impact.
- A UI Toolkit overlay (fragment-count slider, explosion-force slider, trails toggle, fracture-all / restore-all buttons, hotkey legend) authored against the [Unity UI Toolkit Design System](https://github.com/sinanata/unity-ui-document-design-system) — same dark token palette, same `.ds-btn` / `.ds-slider` / `.ds-toast` components used in [Leap of Legends](https://leapoflegends.com).

### Cloning this demo project

The demo's UI consumes the design system as a git submodule (vendored at `Vendor/unity-ui-document-design-system`) and links the drop-in folder into `Assets/DesignSystem` via a per-clone OS link. Pure-runtime consumers of the fracturer (the recipe in [Installation](#installation) below) don't need the design system — only this repo's demo scene does.

```bash
git clone --recurse-submodules https://github.com/sinanata/unity-mesh-fracture
cd unity-mesh-fracture
```

Then create the link from `Assets/DesignSystem` to the vendored copy:

```powershell
# Windows — directory junction (no admin / Developer Mode required)
cmd /c mklink /J Assets\DesignSystem Vendor\unity-ui-document-design-system\Assets\DesignSystem
```

```bash
# macOS / Linux — symbolic link
ln -s ../Vendor/unity-ui-document-design-system/Assets/DesignSystem Assets/DesignSystem
```

The junction / symlink itself is gitignored; each contributor re-runs the command after their first clone. Open in Unity 6000.3.8f1 (or compatible) and press Play in `Assets/Demo/Scenes/MeshFractureDemo.unity`.

If you forgot `--recurse-submodules`, run `git submodule update --init` after the fact, then create the link.

For a minimum-viable example to wire the fracturer into your own scene, read `Assets/MeshFracture/Demo/MeshFractureDemo.cs` — ~140 lines, shows the full pipeline (pre-bake at `Start`, look up at impact, spawn `FractureBurst`).

### Build the WebGL preview locally

```powershell
copy Tools\Build\config.example.json Tools\Build\config.local.json
# Edit unity.windowsEditorPath if Unity isn't in C:\Program Files\Unity\Hub\Editor\6000.3.8f1\
.\Tools\Build\Build-Demo.ps1 -Serve     # build + serve at http://localhost:3000
.\Tools\Build\Build-Demo.ps1 -Deploy    # build + force-push to gh-pages
```

See `Tools/Build/README.md` for the full orchestrator reference (cache recovery, Burst-AOT retry, GitHub Pages first-run setup).

## Requirements

| Requirement | Notes |
| --- | --- |
| **Unity 6** (6000.x or newer) | Tested on 6000.0 and 6000.3. Should work on 2022.3 LTS with minor API tweaks. |
| **URP** (Universal Render Pipeline) | The library configures stock URP/Lit at runtime for transparent + Cull Off. The C# code is pipeline-agnostic — for Built-in / HDRP you only need to wire your own transparent material with a `_BaseColor` alpha that the burst can fade. |
| `com.unity.render-pipelines.universal` | Already present if you started from a URP template. |

No NuGet, no asmdef requirements, no external native libraries.

## Installation

The repo is a complete Unity project (the demo scene above lives here), but the runtime is **one folder** you drop into `Assets/`:

```
your-unity-project/
└── Assets/
    └── MeshFracture/                    ← drop the whole folder
        ├── Runtime/
        │   ├── MeshFragmenter.cs
        │   ├── FragmentCache.cs
        │   └── FractureBurst.cs
        └── Demo/
            └── MeshFractureDemo.cs      ← single-script "drop on a cube" example
```

`Assets/Demo/` is the WebGL preview scene with the 8 fracturable objects — leave it behind when you copy `Assets/MeshFracture/` into your own project.

**Option A — copy files:**

```powershell
git clone https://github.com/sinanata/unity-mesh-fracture ../mesh-fracture-src
cp -r ../mesh-fracture-src/Assets/MeshFracture Assets/MeshFracture
```

**Option B — git submodule:**

```bash
cd your-unity-project
git submodule add https://github.com/sinanata/unity-mesh-fracture Assets/MeshFracture-src
# Symlink or copy Assets/MeshFracture-src/Assets/MeshFracture → Assets/MeshFracture
```

Unity reimports automatically — no shader assets to compile, no `.mat` GUIDs to migrate.

## Quick start

```csharp
using MeshFracture;
using UnityEngine;

public class DestructibleEnemy : MonoBehaviour
{
    public GameObject characterPrefab;     // The 3D model to fracture
    public Material exteriorMaterial;      // Character's skin material
    public Material interiorMaterial;      // "Raw interior" — gore / bone / wood

    private const int FragmentCount = 8;
    private int bakeKey;

    private void Start()
    {
        // Bake on a loading screen, NOT at gameplay time. Voronoi is
        // 3-9 ms per character on desktop, proportionally more on mobile.
        bakeKey = characterPrefab.GetInstanceID() ^ (FragmentCount * 7919);
        FragmentCache.RequestPreBake(bakeKey, characterPrefab, FragmentCount);
    }

    public void Die()
    {
        // Hide the original visual.
        GetComponent<MeshRenderer>().enabled = false;

        // Look up pre-baked fragments. If the bake hasn't finished yet
        // (rare — usually done within one frame of Start), fall back to
        // a smoke puff or particle burst.
        if (!FragmentCache.TryGet(bakeKey, out var cached))
            return;

        // Offset the cached centroids to the death position.
        Vector3 deathPos = transform.position;
        Vector3 offset = deathPos - cached.MeshCenter;
        var fragments = new FragmentResult[cached.Meshes.Length];
        for (int i = 0; i < fragments.Length; i++)
            fragments[i] = new FragmentResult
            {
                Mesh = cached.Meshes[i],
                Centroid = cached.LocalCentroids[i] + offset,
            };

        // Spawn the burst. The component handles gravity, tumble, trails,
        // alpha fade, and self-destruction at the lifetime end.
        var burstGO = new GameObject("FractureBurst");
        burstGO.transform.position = deathPos;
        var burst = burstGO.AddComponent<FractureBurst>();
        // For collision-aware fragments that bounce off the world, also set:
        //   burst.UseUnityPhysics       = true;
        //   burst.PreBakedColliderMeshes = cached.ColliderMeshes;
        //   burst.DissolveAfterSettle   = true;   // fade once chunks come to rest
        burst.Initialize(fragments, exteriorMaterial, interiorMaterial,
            explosionForce: 18f, meshRotation: cached.MeshRotation);
    }
}
```

That's the entire gameplay-side wiring.

> **Materials must be transparent.** The burst animates `_BaseColor.a` 1 → 0 over `DissolveDuration`; an opaque material renders the alpha at full regardless. Configure your URP/Lit materials for transparent + Cull Off + ZWrite-on at runtime — see `Assets/Demo/Runtime/MaterialFactory.cs::ConfigureFractureTransparent` for the exact recipe (10 lines).

## Architecture

```
MeshFragmenter.cs           ← pure C# Voronoi fracturer (zero deps), watertight cells
FragmentCache.cs            ← async pre-bake worker + cooked convex hulls + lookup
FractureBurst.cs            ← per-fragment lifecycle (CPU sim or Unity physics, alpha fade, trails)
```

The library ships zero `.shader` and zero `.mat` assets — fragment materials are stock URP/Lit configured at runtime. See [Materials section](#materials) below.

### `MeshFragmenter`

```csharp
FragmentResult[] Fragment(Mesh source, int count, Vector3 center);
Mesh BakeSkinnedMesh(SkinnedMeshRenderer smr);
```

The fragmenter generates `count` Voronoi seeds in a jittered grid, then for each seed, iteratively half-space-clips the source mesh against the bisecting planes between that seed and every other. Each surviving cell becomes a fragment with two submeshes: 0 = exterior (original surface, original normals), 1 = cap faces (cut surfaces, flat normals along the cap plane).

**Watertightness contract.** Every emitted fragment is a closed polyhedron. The naive "clip the exterior, generate one cap per plane" pattern leaves orphan cap geometry past the cell boundary and fails to seal where two planes' boundary lines meet inside the cell. This implementation maintains a single triangle list with per-triangle submesh tags, re-clips everything against every plane (including caps from earlier planes), and dedups cut-edge intersections by world position before chaining edges into cap polygons — so caps from one plane get correctly clipped + retagged when a later plane crosses them, and the cap-edge graph stays connected even where two cuts meet. Multi-loop cap support handles non-convex source meshes where a single plane intersects the body in disjoint regions.

Fan-triangulates each cap from the first vertex of the chained polygon — works because Voronoi cell caps are convex by construction. Edge cases (sliver geometry, fully-eroded cells) are silently dropped; if every cell degenerates, the source mesh is returned as a single fragment so callers don't need null-handling.

Deterministic from the source mesh + seed center. Same inputs → same output, frame after frame.

### `FragmentCache`

```csharp
void RequestPreBake(int key, GameObject modelPrefab, int fragmentCount);
void BakeSynchronous(int key, Mesh sourceMesh, Quaternion rot, int fragmentCount);
bool TryGet(int key, out CachedData data);
void Evict(int key);  void Clear();

struct CachedData {
    Mesh[]      Meshes;          // fragment meshes (visual)
    Mesh[]      ColliderMeshes;  // pre-cooked convex hulls (paired 1:1)
    Vector3[]   LocalCentroids;  // local to MeshCenter
    Vector3     MeshCenter;      // source bounds center
    Quaternion  MeshRotation;    // source SMR / MeshFilter rotation
}
```

The async path runs one bake per frame on a coroutine — multiple `RequestPreBake` calls during scene load won't stack into one big spike. Reads the prefab's bind-pose `sharedMesh` immediately and multiplies vertices by `lossyScale` so fragments match the in-scene size of the model, not the bind-pose size.

The synchronous path blocks the main thread for the full Voronoi cost. Use it during loading screens when you'd rather pay the time up front and avoid the "bake hasn't finished" branch.

**Pre-cooked collider meshes.** Both paths position-dedup each fragment's vertex array (the cap-flat-normal duplication doubles the input to Unity's hull algorithm without contributing unique geometry) and run `Physics.BakeMesh(convex: true)` on the deduped result during the worker frame. The cooked PhysX shape lives on the mesh; consumers assigning `cached.ColliderMeshes[i]` to `MeshCollider.sharedMesh` reuse it instead of paying for the hull-cook on the spawn frame. For dense source meshes (skinned characters), this is the single biggest contributor to per-fracture cost.

### `FractureBurst`

```csharp
void Initialize(FragmentResult[] fragments, Material exterior, Material interior,
                float explosionForce = 18f, float trailWidth = 0.12f,
                Gradient trailGradient = null, Quaternion meshRotation = default);

// Lifetime
float Lifetime = 5f;             // target burst duration; hard safety net at 2× Lifetime
float DissolveDuration = 1f;     // alpha fade window — animates _BaseColor.a 1 → 0

// CPU sim — defaults
Vector3 GravityVector = (0,-25,0); // CPU sim only; physics mode uses Physics.gravity
bool    LockToXY = false;          // CPU sim only — locks Z + tumble for side-on 2D

// Trails
bool EnableTrails = true;          // false = skip TrailRenderer setup (mobile perf)

// Optional Unity physics — fragments get Rigidbody + convex MeshCollider
bool  UseUnityPhysics = false;
float Bounciness = 0.35f;
float Friction = 0.5f;
float FragmentMass = 0.5f;

// Optional settle-based dissolve (with time-based fallback at Lifetime - DissolveDuration)
bool  DissolveAfterSettle = false;
float SettleHoldDuration = 1f;

// Optional pre-cooked convex hulls (paired 1:1 with fragments)
Mesh[] PreBakedColliderMeshes;

// Read-only — true once the progressive-build phase finishes
bool IsFullyInitialized { get; }
```

Each fragment is a child GameObject with `MeshFilter` + `MeshRenderer` + (optional) `TrailRenderer` + (physics mode) `Rigidbody` + `MeshCollider`. The progressive build instantiates `BUILD_BATCH_SIZE` fragments per `Update` tick rather than all at once, so an 8-fragment burst with full physics + trails spreads its 50–180 ms allocation cost across ~4 frames instead of dropping a single one.

**CPU sim path.** Gravity + tumble integration in a single loop, cheap floor at `y = -1`. Quaternion derivative integration (`q + (½ω·dt) ⊗ q`) keeps tumble stable at high angular velocities without gimbal-lock artefacts. No `Rigidbody`, no `Physics.Simulate`, no scene-wide allocations.

**Unity physics path.** Each fragment gets a convex `MeshCollider` (or a `BoxCollider` for fragments whose deduped vert count exceeds the 100-vert convex-hull ceiling) plus a `Rigidbody`. Pair-wise `IgnoreCollision` between sibling fragments runs once after the build completes — without it, the depenetration impulse on shared cell boundaries launches half the burst skyward at spawn. Fragments still collide with all non-fragment scene geometry.

The default 5-second lifetime + 1-second dissolve fade matches LoL's "boom, debris, gone" cadence; the WebGL demo bumps these to 12 s + 1.2 s with `DissolveAfterSettle = true` so chunks come to rest before fading. Tune for your game.

### Materials

The library is shader-agnostic: `FractureBurst` only requires that the materials you hand to `Initialize()` expose `_BaseColor` (alpha is animated 1 → 0 over `DissolveDuration`). The recommended path is **stock URP/Lit configured at runtime for transparent + Cull Off + ZWrite-on**. The exact 10-line configuration lives in `Assets/Demo/Runtime/MaterialFactory.cs::ConfigureFractureTransparent`:

```csharp
mat.SetFloat("_Surface",   1f);   // Transparent
mat.SetFloat("_Blend",     0f);   // Alpha
mat.SetFloat("_Cull",      0f);   // Off — both faces render
mat.SetFloat("_ZWrite",    1f);   // ON — closer face wins (see below)
mat.SetFloat("_SrcBlend",  (float)BlendMode.SrcAlpha);
mat.SetFloat("_DstBlend",  (float)BlendMode.OneMinusSrcAlpha);
mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
mat.SetOverrideTag("RenderType", "Transparent");
mat.renderQueue = (int)RenderQueue.Transparent;
```

This replicates what URP/Lit's editor inspector calls when you flip the Surface dropdown to Transparent — but `BaseShaderGUI.SetupMaterialBlendModeAndDepthWriteAndDepthTest` doesn't run for `new Material(shader)` instances, so the per-pass blend state, keyword, and render queue must be set explicitly.

**ZWrite-on in a transparent pass** is non-standard but **load-bearing for the chunk-debris look**. With Cull Off both faces of a fragment rasterize, and ZWrite Off would let "whichever triangle the GPU happened to draw last" win the pixel — that's back-face lighting (unlit interior) about half the time, presenting as the "see-through to interior" symptom on solid chunks. ZWrite On means the depth test selects the closer face regardless of rasterization order.

**Why no custom shader.** An earlier iteration shipped a hand-rolled Lambert + alpha URP shader. Three rounds of WebGL2-specific fixes (texture default binding, shadow-keyword strip, CBUFFER reorder to mirror URP/Unlit) all rendered correctly on D3D11 / Metal / Vulkan and the editor while continuing to render every fragment solid black on WebGL2 in Chrome. Pre-fracture URP/Lit primitives in the SAME build rendered correctly throughout, isolating the bug to the custom shader's HLSL → GLSL ES 3.0 cross-compilation. Stock URP/Lit sidesteps the cross-compiler entirely; the custom shader was removed. The runtime now ships zero `.shader` assets.

## What makes this robust

- **Watertight Voronoi cells.** Cap polygons from earlier clip planes get re-clipped + re-tagged when later planes cross them; cut-edge intersections dedup by world position before chaining; multi-loop ChainEdges handles non-convex source meshes where one plane crosses the body in disjoint regions. The naive "clip the exterior, generate one cap per plane" pattern produces orphan geometry and see-through cells; this implementation seals around the entire convex intersection.
- **Low per-impact GC.** All Voronoi allocation happens at bake time. The impact path looks up cached meshes + cooked PhysX hulls and instantiates prebuilt shapes. Progressive fragment build (`BUILD_BATCH_SIZE = 2` per `Update` tick) keeps the per-frame allocation cost bounded — a single 8-fragment burst with full physics doesn't spike past the 16 ms budget.
- **Two-submesh fragments.** Most fracture libraries paint the same material on the cuts as on the surface — the result reads as "weird seams". This system gives the cap faces their own submesh + normal, so a character's textured skin and the bloody interior render with the right shader for each.
- **CPU simulation by default; optional Unity physics.** Default mode integrates gravity and tumble on the CPU with a cheap `y = -1` floor — zero scene interaction, no `Rigidbody`, no `Physics.Simulate`. Set `UseUnityPhysics = true` and each fragment gets a convex `MeshCollider` + `Rigidbody` so chunks bounce off any collider in the scene; pair-wise `IgnoreCollision` handles the depenetration impulse on shared cell boundaries that would otherwise launch half the burst skyward at spawn.
- **Pre-cooked convex hulls.** When physics mode is on, `FragmentCache` runs `Physics.BakeMesh(convex: true)` on every fragment during the pre-bake worker frame. The cooked PhysX shape lives on the mesh; `MeshCollider.sharedMesh = cached.ColliderMeshes[i]` reuses it instead of paying for the hull-cook on the spawn frame. On dense source meshes this is the difference between a 5–10 ms hitch per fragment and zero.
- **Settle-aware dissolve with lifetime fallback.** `DissolveAfterSettle = true` waits for every fragment to come to rest for `SettleHoldDuration` before fading. If a fragment never settles (slides off a stage, bounces forever), the time-based fallback at `Lifetime − DissolveDuration` always fires so chunks fade rather than pop. Both paths drive the same `_BaseColor.a` 1 → 0 animation.
- **`AnimationCurve` with linear tangents.** Trails use a noisy width curve with linear in/out tangents. Default smooth tangents would sand the bumps into invisible gentle waves; the verbose tangent setup is what makes trails read as "spurts".
- **Quaternion derivative integration.** Each fragment's CPU-sim rotation integrates as `q + (½ω·dt) ⊗ q` rather than `Quaternion.Euler(angVel * dt)`. Keeps the simulation stable at high angular velocities and avoids gimbal-lock-style artefacts on tumbling fragments.
- **Mobile knob.** `EnableTrails = false` skips the TrailRenderer setup entirely. Combined with `fragmentCount = 3` in your bake, you get a fragmenting effect that runs cleanly on lower-end Android.

The whole runtime is ~1.7k lines of C# (`MeshFragmenter` + `FragmentCache` + `FractureBurst`) + ~220 lines of HLSL — small enough to read in one sitting. Half of the comments are documentation of the trade-offs.

## When NOT to use this

- **Heavy stack of breakables.** Each fragment is a separate GameObject with its own MeshFilter + MeshRenderer. 8 fragments × 30 enemies dying in a wave = 240 GameObjects spawning in one frame — fine, but if you're shattering an entire stadium of breakables you'll want a custom indirect-instanced renderer.
- **Re-fracturing fragments.** This is single-stage. The fragments don't break further on impact. Sub-fragmentation requires a CSG-aware fracturer (this one isn't) — try Obi or RayFire.
- **Persistent debris.** The fade-then-destroy lifecycle is the design. `Lifetime` is configurable, but debris that stays on the ground forever needs a different system (LOD, batching, eventual cleanup) — fragments aren't combined, batched, or LOD'd.
- **Non-URP pipelines.** The C# is pipeline-agnostic but the runtime material setup is URP-only. For Built-in / HDRP, wire your own transparent + Cull Off material with a `_BaseColor` alpha the burst can drive — the C# fade pass uses `mat.SetColor("_BaseColor", c)` and only assumes that property exists.

## Contributing

Issues and PRs welcome. The whole runtime is small enough to read in one sitting.

Areas where help is especially useful:

- **HDRP / Built-in pipeline support.** Today the runtime material setup targets URP/Lit. An equivalent helper for HDRP/Lit (or a Built-in surface-shader path) would let consumers pick the same fade behaviour without rolling their own — the C# code is pipeline-agnostic, only `MaterialFactory.ConfigureFractureTransparent` needs alternates.
- **Unit tests for `MeshFragmenter`.** A few canonical inputs (cube, sphere, irregular convex, concave) with expected fragment counts would catch regressions on the clip / cap math.
- **Editor tool that bakes fragments to assets.** Useful for source-controlled determinism and avoiding the runtime bake entirely.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the PR checklist.

## Credits & support

Made for **[Leap of Legends](https://leapoflegends.com)** — a cross-platform physics-heavy multiplayer game in active development, targeting Steam, iOS, Android, and Mac. If this saved you time:

- ⭐ Star the repo
- 🎮 [Wishlist Leap of Legends on Steam](https://store.steampowered.com/app/2269500/) — mobile store pages coming soon
- 🐦 Shout out [@sinanata](https://x.com/sinanata)

## Licence

MIT — see [LICENSE](LICENSE). Free for commercial use. No warranty.

The 3D models in `Assets/Demo/Resources/Models/` are by [Kenney](https://kenney.nl) and licensed under [CC0](https://creativecommons.org/publicdomain/zero/1.0/) — see `Assets/Demo/Resources/Models/CREDITS.txt` for the per-asset attribution. The demo scene is independent of the fracturer; if you only want the runtime, copy `Assets/MeshFracture/` and ignore `Assets/Demo/`.

---

**[Leap of Legends](https://leapoflegends.com)** · physics · multiplayer · cross-platform · in development · the destruction effects in every gameplay clip use this pipeline.
