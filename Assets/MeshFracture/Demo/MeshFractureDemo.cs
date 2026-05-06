using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MeshFracture.Demo
{
    /// <summary>
    /// Drop this on a GameObject with a <see cref="MeshFilter"/> and a
    /// <see cref="MeshRenderer"/>. Press <c>Space</c> at runtime to shatter
    /// the mesh in place — original is hidden, fragments spawn, fade out, and
    /// self-destruct at lifetime end.
    ///
    /// Demonstrates the full pipeline: pre-bake at <c>Start</c>, look up + spawn
    /// at trigger time. For runtime gameplay you'd typically pre-bake every
    /// destructible model on a loading screen, not from <c>Start</c>.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class MeshFractureDemo : MonoBehaviour
    {
        [Tooltip("Number of Voronoi cells. 3 = 'split apart'. 8 = 'shattered'.")]
        public int FragmentCount = 8;

        [Tooltip("Outward speed in units/sec applied to each fragment at the moment of impact.")]
        public float ExplosionForce = 18f;

        [Tooltip("Material applied to the original surface (submesh 0). Defaults to the renderer's first material.")]
        public Material ExteriorMaterial;

        [Tooltip("Material applied to the cap faces (submesh 1) — the freshly-cut interior look.")]
        public Material InteriorMaterial;

        [Tooltip("Lifetime in seconds before fragments are destroyed.")]
        public float Lifetime = 5f;

#if ENABLE_INPUT_SYSTEM
        [Tooltip("Trigger key. Defaults to Space.")]
        public Key TriggerKey = Key.Space;
#else
        [Tooltip("Trigger key. Defaults to Space.")]
        public KeyCode TriggerKey = KeyCode.Space;
#endif

        private int bakeKey;
        private bool fired;

        private void Start()
        {
            // Bake key = instance ID of the source mesh + fragment count.
            // Different fragment counts → different bakes; same source mesh
            // and count → cache hit.
            var sourceMesh = GetComponent<MeshFilter>().sharedMesh;
            bakeKey = sourceMesh.GetInstanceID() ^ (FragmentCount * 7919);

            FragmentCache.RequestPreBake(bakeKey, gameObject, FragmentCount);

            if (ExteriorMaterial == null)
                ExteriorMaterial = GetComponent<MeshRenderer>().sharedMaterial;
            if (InteriorMaterial == null)
            {
                // Default interior = a darker URP/Lit material configured for
                // transparent + Cull Off so the burst's _BaseColor.a fade
                // animates and both sides of cap faces render. Same recipe
                // the WebGL demo uses (see Assets/Demo/Runtime/MaterialFactory
                // .cs::ConfigureFractureTransparent for the longer "why").
                var shader = Shader.Find("Universal Render Pipeline/Lit")
                             ?? Shader.Find("Universal Render Pipeline/Simple Lit");
                if (shader != null)
                {
                    InteriorMaterial = new Material(shader);
                    InteriorMaterial.SetColor("_BaseColor", new Color(0.4f, 0.02f, 0.02f, 1f));
                    // Bind a non-null white texture explicitly: on WebGL2 a
                    // runtime-created Material whose _BaseMap relies on the
                    // shader's `= "white" {}` default sometimes samples as
                    // (0,0,0,0) → fragments render solid black.
                    InteriorMaterial.SetTexture("_BaseMap", Texture2D.whiteTexture);
                    InteriorMaterial.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));
                    ConfigureFractureTransparent(InteriorMaterial);
                }
                else
                {
                    // URP not installed — fall back to the exterior. Fade
                    // visibility on the caps degrades, but the burst still spawns.
                    InteriorMaterial = ExteriorMaterial;
                }
            }
        }

        // URP/Lit transparent + Cull Off runtime configuration. URP/Lit's
        // editor inspector calls `BaseShaderGUI.SetupMaterialBlendModeAndDepth
        // WriteAndDepthTest` whenever the Surface dropdown changes, but that
        // path doesn't fire for `new Material(shader)` — replicate it here.
        // ZWrite stays ON because Cull Off + ZWrite Off lets back-face-lit
        // triangles win the depth race ("see-through to interior" symptom).
        static void ConfigureFractureTransparent(Material mat)
        {
            mat.SetFloat("_Surface",   1f);   // Transparent
            mat.SetFloat("_Blend",     0f);   // Alpha
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_Cull",      0f);   // Off — both faces render
            mat.SetFloat("_SrcBlend",  (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend",  (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite",    1f);   // ON — closer face wins
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private void Update()
        {
            if (fired) return;
#if ENABLE_INPUT_SYSTEM
            // New Input System path: keyboard may be null on touch-only
            // mobile, in which case there's no way to fire this trigger
            // anyway — caller would need a UI button or InputAction.
            var kb = Keyboard.current;
            if (kb == null || !kb[TriggerKey].wasPressedThisFrame) return;
#else
            if (!Input.GetKeyDown(TriggerKey)) return;
#endif
            if (!FragmentCache.TryGet(bakeKey, out var cached))
            {
                Debug.LogWarning("[MeshFractureDemo] Fragments not yet baked. Wait a frame after Start().");
                return;
            }

            fired = true;

            // Hide the original mesh — without this you'd see the unbroken
            // mesh and the fragments overlapping. Disable the renderer rather
            // than destroying the GameObject so this demo keeps the trigger
            // hooked up.
            GetComponent<MeshRenderer>().enabled = false;

            // Build the fragment array, offset to this GameObject's world
            // position. Cached centroids are local to the source mesh's
            // bounds center — adding (transform.position - meshCenter) puts
            // them in world space.
            Vector3 deathPos = transform.position;
            var fragments = new FragmentResult[cached.Meshes.Length];
            Vector3 offset = deathPos - cached.MeshCenter;
            for (int i = 0; i < fragments.Length; i++)
            {
                fragments[i] = new FragmentResult
                {
                    Mesh = cached.Meshes[i],
                    Centroid = cached.LocalCentroids[i] + offset,
                };
            }

            var burstGO = new GameObject("FractureBurst");
            burstGO.transform.position = deathPos;
            var burst = burstGO.AddComponent<FractureBurst>();
            burst.Lifetime = Lifetime;
            burst.Initialize(
                fragments,
                ExteriorMaterial,
                new Material(InteriorMaterial),  // copy so dissolve PropertyBlocks don't smear across instances
                explosionForce: ExplosionForce,
                meshRotation: cached.MeshRotation
            );
        }
    }
}
