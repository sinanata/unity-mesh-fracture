using System.Collections.Generic;
using UnityEngine;

namespace MeshFractureDemo
{
    /// <summary>
    /// Defines the eight fracturable objects on the demo stage. Mixing built-in
    /// primitives (cube, sphere) with kenney pirate-kit FBXes (barrel, crate,
    /// chest, cannon, rocks, characterMedium) covers the algorithm's range:
    /// convex / concave, low-poly / dense, static-mesh / skinned. Each entry
    /// owns its construction so new demo objects only need a single block here.
    ///
    /// All kenney assets are CC0 (Pirate Kit 2.0 + Animated Characters 1) and
    /// share the same colormap atlas at <c>Assets/Demo/Resources/Models/Textures/colormap.png</c>.
    /// </summary>
    public static class DemoTargetCatalog
    {
        public class Definition
        {
            public string DisplayName;
            public System.Func<GameObject> Build;
            public int    DefaultFragmentCount = 8;
            public float  Scale = 1f;
            public Vector3 RotationEuler = Vector3.zero;
            // Interior tint for the cap material — what fresh
            // splits look like for THIS object (gore for character, raw wood
            // for crate, fresh stone for rocks). One of the algorithm's
            // payoffs is that submesh 1 can be styled independently of the
            // outer skin.
            public Color  InteriorColor = new Color(0.22f, 0.04f, 0.04f);
            public bool   IsSkinned;
            // Replace the visual mesh with its bounding-box solid before
            // fragmenting. Use for hollow / carved geometry (open crates,
            // chests with thin walls) where the original mesh has no
            // interior volume — without this, Voronoi cells produce
            // paper-thin shell pieces because the source has no triangles
            // between the inner and outer surfaces. Pre-fracture visual
            // still uses the original mesh; only the fragmentation source
            // is swapped.
            public bool   SolidifyForFragment;
        }

        public static List<Definition> GetDefinitions()
        {
            var list = new List<Definition>();

            // ── Primitives ────────────────────────────────────────────────
            list.Add(new Definition
            {
                DisplayName = "Cube",
                Scale = 1f,
                DefaultFragmentCount = 8,
                InteriorColor = new Color(0.7f, 0.25f, 0.18f),
                Build = () => BuildPrimitive(PrimitiveType.Cube,
                    new Color(0.95f, 0.40f, 0.32f)),
            });

            list.Add(new Definition
            {
                DisplayName = "Sphere",
                Scale = 1f,
                DefaultFragmentCount = 8,
                InteriorColor = new Color(0.10f, 0.30f, 0.55f),
                Build = () => BuildPrimitive(PrimitiveType.Sphere,
                    new Color(0.34f, 0.62f, 0.94f)),
            });

            // ── Kenney pirate-kit static meshes ────────────────────────────
            // Models share one 256x256 colormap atlas — `KenneyColormap.mat`
            // applies to all of them so we don't ship six near-identical
            // .mat assets. Interior tint per object so the fresh-cut faces
            // read like the right material.
            list.Add(KenneyDef("Barrel",  "barrel",  scale: 2.4f,
                interior: new Color(0.30f, 0.16f, 0.08f)));   // wood
            // Crate + chest are open-topped boxes with thin walls — the
            // imported mesh has both inner and outer wall triangles but
            // no volume between them. Voronoi-fragmenting the raw mesh
            // alone produces paper-thin shell pieces. solidifyForFragment
            // tells DemoTarget.BuildSolidifiedMergedMesh to merge an
            // interior filler box into the source so fragments span both
            // the carved walls (preserving silhouette) AND solid wood
            // mass behind them — chunks come out thick without losing
            // the crate's outer shape.
            list.Add(KenneyDef("Crate",   "crate",   scale: 2.6f,
                interior: new Color(0.45f, 0.26f, 0.12f),
                solidify: true));                              // wood
            list.Add(KenneyDef("Chest",   "chest",   scale: 2.6f,
                interior: new Color(0.55f, 0.35f, 0.15f),
                solidify: true));                              // wood
            list.Add(KenneyDef("Cannon",  "cannon",  scale: 2.0f,
                interior: new Color(0.18f, 0.18f, 0.20f),     // iron
                rotation: new Vector3(0f, 90f, 0f)));
            list.Add(KenneyDef("Rocks",   "rocks-a", scale: 2.6f,
                interior: new Color(0.35f, 0.34f, 0.32f)));   // stone

            // ── Skinned mesh — kenney animated character ───────────────────
            // Demonstrates the SMR path: bind-pose mesh × lossyScale fed
            // into MeshFragmenter, mirroring LoL's FragmentCache. We don't
            // route through BakeMesh here because kenney's prefab bakes
            // scale-100 into multiple intermediate transforms — see
            // DemoTarget.BakeSkinnedSync for the full "why".
            list.Add(new Definition
            {
                DisplayName = "Character",
                Scale = 1.3f,
                DefaultFragmentCount = 10,
                IsSkinned = true,
                InteriorColor = new Color(0.65f, 0.06f, 0.06f), // gore
                Build = BuildKenneyCharacter,
            });

            // ── Sprite destructibles — joint sprite-baker × mesh-fracture ──
            // Each is the same Kenney AC2 character baked to a single-frame
            // sprite atlas (different skin per instance), then displayed on
            // a thin-box mesh whose front face carries the atlas UVs. The
            // Voronoi fracturer cuts the thin box; each fragment inherits
            // a slice of the original UVs, so the burst plays as the sprite
            // shattering into 2D pieces. See SpriteCharacterTarget for the
            // bake → mesh-swap orchestration.
            //
            // The interior tint reads as the back-face / cut-edge colour of
            // a tumbling sprite chunk; a desaturated dark grey blends with
            // the typical Kenney palette without competing with the
            // character's exterior colours.
            list.Add(SpriteCharDef("Sprite Skater M",  "skaterMaleA"));
            list.Add(SpriteCharDef("Sprite Skater F",  "skaterFemaleA"));
            list.Add(SpriteCharDef("Sprite Criminal",  "criminalMaleA"));
            list.Add(SpriteCharDef("Sprite Cyborg",    "cyborgFemaleA"));

            return list;
        }

        static Definition SpriteCharDef(string display, string skinResource)
        {
            return new Definition
            {
                DisplayName          = display,
                Scale                = 1f,                // SpriteCharacterTarget owns dimensions
                DefaultFragmentCount = 8,
                InteriorColor        = new Color(0.20f, 0.20f, 0.22f),
                IsSkinned            = false,             // source is a thin-box static mesh
                SolidifyForFragment  = false,             // already has volume (~0.12u thick)
                Build                = () => BuildSpriteCharacter(display, skinResource),
            };
        }

        static GameObject BuildSpriteCharacter(string display, string skinResourceName)
        {
            var go = new GameObject(display);

            var capturePrefab = Resources.Load<GameObject>("Models/characterMedium");
            var skin          = Resources.Load<Texture2D>($"Models/Skins/{skinResourceName}");
            if (capturePrefab == null || skin == null)
            {
                Debug.LogWarning($"[MeshFractureDemo] Sprite character {display}: missing prefab or skin (Models/Skins/{skinResourceName}). Falling back to a placeholder cube.");
                Object.Destroy(go);
                var fallback = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fallback.name = display;
                Object.Destroy(fallback.GetComponent<Collider>());
                return fallback;
            }

            // Bake key derived from the display name so it's stable across
            // scene reloads and unique per (character, skin) combo. The
            // SpriteAtlasBaker dedups requests by key, so multiple instances
            // of the same skin would share one atlas — but we use distinct
            // skins so each gets its own bake.
            int seed = display.GetHashCode();
            var ctrl = go.AddComponent<SpriteCharacterTarget>();
            ctrl.Setup(capturePrefab, skin, display, seed);
            return go;
        }

        // =================================================================
        // Construction helpers
        // =================================================================

        static Definition KenneyDef(string display, string resourceName,
            float scale, Color interior, Vector3 rotation = default,
            bool solidify = false)
        {
            return new Definition
            {
                DisplayName = display,
                Scale = scale,
                RotationEuler = rotation,
                InteriorColor = interior,
                SolidifyForFragment = solidify,
                Build = () => BuildKenneyMesh(display, resourceName, scale, rotation),
            };
        }

        static GameObject BuildPrimitive(PrimitiveType type, Color tint)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = type.ToString();
            // Strip the auto-added collider — DemoBootstrap attaches its own
            // BoxCollider sized to the visual bounds for raycast click.
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = MaterialFactory.PrimitiveMaterial(tint);
            return go;
        }

        static GameObject BuildKenneyMesh(string display, string resourceName,
            float scale, Vector3 rotation)
        {
            // Models live in `Assets/Demo/Resources/Models/{name}` after import.
            // FBX imports as a hierarchy (root -> mesh node) — we duplicate the
            // whole prefab so the original asset stays untouched.
            var prefab = Resources.Load<GameObject>($"Models/{resourceName}");
            GameObject host;
            if (prefab == null)
            {
                Debug.LogWarning($"[MeshFractureDemo] Missing kenney model: Resources/Models/{resourceName} — falling back to a cube.");
                host = GameObject.CreatePrimitive(PrimitiveType.Cube);
                host.name = display;
                Object.Destroy(host.GetComponent<Collider>());
                return host;
            }

            host = Object.Instantiate(prefab);
            host.name = display;
            host.transform.localScale = Vector3.one * scale;
            host.transform.rotation = Quaternion.Euler(rotation);

            // Apply the shared kenney colormap material to every renderer in
            // the imported hierarchy. The .fbx ships with a "colormap" sub-
            // material reference; without re-binding, Unity falls back to the
            // pink "missing material" shader.
            ApplyKenneyMaterial(host);
            return host;
        }

        static GameObject BuildKenneyCharacter()
        {
            var prefab = Resources.Load<GameObject>("Models/characterMedium");
            if (prefab == null)
            {
                Debug.LogWarning("[MeshFractureDemo] Missing characterMedium — falling back to a capsule.");
                var fallback = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                fallback.name = "Character";
                Object.Destroy(fallback.GetComponent<Collider>());
                return fallback;
            }

            var host = Object.Instantiate(prefab);
            host.name = "Character";
            host.transform.localScale = Vector3.one * 1.3f;

            var skin = Resources.Load<Texture2D>("Models/Skins/skaterMaleA");
            ApplyCharacterSkin(host, skin);
            return host;
        }

        static void ApplyKenneyMaterial(GameObject root)
        {
            var mat = MaterialFactory.KenneyColormapMaterial();
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = new Material[mr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                mr.sharedMaterials = mats;
            }
        }

        static void ApplyCharacterSkin(GameObject root, Texture2D skin)
        {
            var mat = MaterialFactory.CharacterSkinMaterial(skin);
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mats = new Material[smr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                smr.sharedMaterials = mats;
            }
            // Some kenney character imports also have static MeshRenderers
            // (extra equipment) — paint those too.
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = new Material[mr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                mr.sharedMaterials = mats;
            }
        }
    }
}
