using UnityEngine;

namespace MeshFractureDemo
{
    /// <summary>
    /// Programmatic material factory. The demo deliberately avoids shipping
    /// .mat assets — they bring GUID drift and Material Inspector temptations.
    /// Building materials in C# keeps the runtime constructor visible and
    /// fixes "the material is pink" import bugs at the source.
    ///
    /// All materials — pre-fracture meshes AND post-fracture chunks — use
    /// URP's stock `Universal Render Pipeline/Lit`. Pre-fracture meshes
    /// render with Lit's default opaque + Cull Back surface; post-fracture
    /// fragments are configured for transparent + Cull Off via
    /// <see cref="ConfigureFractureTransparent"/> so they can fade out
    /// uniformly while the chunk stays solid from any viewing angle.
    /// </summary>
    public static class MaterialFactory
    {
        const string LIT_SHADER       = "Universal Render Pipeline/Lit";
        const string SIMPLE_LIT       = "Universal Render Pipeline/Simple Lit";
        const string COLORMAP_PATH    = "Models/Textures/colormap";

        // Why fragments use URP/Lit
        // -------------------------
        // An earlier iteration shipped a custom Lambert URP shader. Three
        // rounds of WebGL2-specific fixes (Texture2D.whiteTexture default
        // binding, shadow-sampler keyword strip, CBUFFER reorder to mirror
        // URP/Unlit) all rendered correctly on D3D11 / Metal / Vulkan and
        // the editor, but every fragment continued to render solid black
        // on WebGL2 in Chrome. URP/Lit-driven primitives in the SAME build
        // rendered correctly throughout — the bug lives inside the custom
        // shader's HLSL → GLSL ES 3.0 cross-compilation, not the surrounding
        // pipeline. Switching to stock URP/Lit sidesteps the cross-compiler
        // entirely. Fragment counts are small (default 8, max 24) and
        // chunks live for ≤12 s, so the slightly fuller PBR pipeline is
        // invisible cost-wise.
        //
        // Fade is driven by `_BaseColor.a` (URP/Lit's surface alpha when
        // `_Surface == 1`) — see FractureBurst's Update() pass.

        // Cached shared materials so 8 demo objects don't allocate 8 copies.
        static Material _kenneyMat;
        static Material _groundMat;
        static Material _pedestalMat;

        // ── Pedestal / ground ─────────────────────────────────────────────

        public static Material PedestalMaterial()
        {
            if (_pedestalMat != null) return _pedestalMat;
            _pedestalMat = MakeLit(new Color(0.18f, 0.21f, 0.27f), smoothness: 0.15f);
            _pedestalMat.name = "Pedestal";
            return _pedestalMat;
        }

        public static Material GroundMaterial()
        {
            if (_groundMat != null) return _groundMat;
            _groundMat = MakeLit(new Color(0.083f, 0.108f, 0.158f), smoothness: 0.0f);
            _groundMat.name = "Ground";
            return _groundMat;
        }

        // ── Showcase wall ─────────────────────────────────────────────────
        // Cool stone tone, slightly desaturated so the orange / red
        // fragments visibly contrast against it on impact.
        static Material _wallMat;
        public static Material WallMaterial()
        {
            if (_wallMat != null) return _wallMat;
            _wallMat = MakeLit(new Color(0.32f, 0.34f, 0.40f), smoothness: 0.10f);
            _wallMat.name = "ShowcaseWall";
            return _wallMat;
        }

        // ── Primitives (cube / sphere) ────────────────────────────────────

        public static Material PrimitiveMaterial(Color tint)
        {
            // One material per primitive so each can have its own color block.
            var mat = MakeLit(tint, smoothness: 0.4f);
            mat.name = $"Primitive_{ColorUtility.ToHtmlStringRGB(tint)}";
            return mat;
        }

        // ── Kenney pirate-kit colormap (shared atlas) ──────────────────────

        public static Material KenneyColormapMaterial()
        {
            if (_kenneyMat != null) return _kenneyMat;
            var tex = Resources.Load<Texture2D>(COLORMAP_PATH);
            if (tex == null)
            {
                Debug.LogWarning($"[MeshFractureDemo] Missing kenney colormap at Resources/{COLORMAP_PATH} — falling back to white.");
                _kenneyMat = MakeLit(Color.white, smoothness: 0.2f);
                _kenneyMat.name = "KenneyColormap_Fallback";
                return _kenneyMat;
            }
            _kenneyMat = MakeLit(Color.white, smoothness: 0.2f);
            _kenneyMat.name = "KenneyColormap";
            _kenneyMat.SetTexture("_BaseMap", tex);
            // The colormap is a 256x256 palette atlas — enable point filtering
            // would muddy the colour seams between palette cells. Bilinear is
            // already the default; this just records the intent.
            tex.filterMode = FilterMode.Bilinear;
            return _kenneyMat;
        }

        // ── Character skin (kenney animated-characters-1) ──────────────────

        public static Material CharacterSkinMaterial(Texture2D skin)
        {
            var mat = MakeLit(Color.white, smoothness: 0.18f);
            mat.name = "CharacterSkin";
            if (skin != null)
            {
                mat.SetTexture("_BaseMap", skin);
                skin.filterMode = FilterMode.Bilinear;
            }
            return mat;
        }

        // ── Exterior fragment material — URP/Lit transparent + Cull Off ────
        //
        // LoL's destructible body parts use a single shader (DestructibleLit
        // with Cull Off) on BOTH submeshes so a tumbling chunk reads as
        // solid: the back-facing triangles on the exterior render too,
        // instead of getting culled and showing the camera straight through
        // to the far side of the fragment. The OSS demo follows the same
        // pattern via `ConfigureFractureTransparent` — set up URP/Lit with
        // `_Surface = 1` (transparent), `_Cull = 0` (off), and `_ZWrite = 1`
        // (we still want the closer face to win the depth test even though
        // the queue is transparent — the alternative is back-face wins-by-
        // draw-order, which presents as "see-through to interior" on solid
        // chunks).
        //
        // We transfer the source's base color + base map onto the new
        // material so the kenney atlas / character skin / primitive tint
        // all carry through to the fragments.
        public static Material ExteriorFractureMaterial(Material source)
        {
            var shader = Shader.Find(LIT_SHADER) ?? Shader.Find(SIMPLE_LIT);
            if (shader == null)
            {
                Debug.LogWarning($"[MeshFractureDemo] URP Lit shader not found — exterior fragments fall back to source material (no dissolve, back-face culled).");
                return source != null ? source : MakeLit(Color.white, smoothness: 0.3f);
            }

            var mat = new Material(shader);
            mat.name = source != null ? $"FractureExt_{source.name}" : "FractureExt";

            // Bind a non-null white texture and identity tiling first, so a
            // source with no _BaseMap (a primitive that's just a tinted
            // URP/Lit material) still gets a valid sampler binding. On
            // WebGL2, runtime-created materials whose texture properties
            // rely solely on the shader's `= "white"` default sometimes
            // sample as (0,0,0,0); this explicit binding sidesteps that
            // path. (Belt-and-suspenders — moving to URP/Lit already
            // resolves the worst case, but the bind is essentially free.)
            mat.SetTexture("_BaseMap", Texture2D.whiteTexture);
            mat.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));

            // Default the tint to white so a null-source fallback chunk
            // still renders as a neutral grey rather than a tinted ghost.
            mat.SetColor("_BaseColor", Color.white);

            if (source != null)
            {
                if (source.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", source.GetColor("_BaseColor"));
                else if (source.HasProperty("_Color"))
                    mat.SetColor("_BaseColor", source.GetColor("_Color"));

                // URP/Lit serializes the texture under _BaseMap, legacy under
                // _MainTex. Try every common alias so the kenney colormap
                // atlas, the character's skin texture, and a built-in URP/Lit
                // on a primitive all transfer.
                Texture tex = null;
                if (source.HasTexture("_BaseMap"))      tex = source.GetTexture("_BaseMap");
                if (tex == null && source.HasTexture("_MainTex")) tex = source.GetTexture("_MainTex");
                if (tex != null)
                {
                    mat.SetTexture("_BaseMap", tex);
                    if (source.HasProperty("_BaseMap_ST"))
                        mat.SetVector("_BaseMap_ST", source.GetVector("_BaseMap_ST"));
                }
            }

            ConfigureFractureTransparent(mat);
            return mat;
        }

        // ── Interior cap material — URP/Lit transparent + Cull Off ─────────

        public static Material InteriorMaterial(Color tint)
        {
            var shader = Shader.Find(LIT_SHADER) ?? Shader.Find(SIMPLE_LIT);
            if (shader == null)
            {
                Debug.LogWarning($"[MeshFractureDemo] URP Lit shader not found — interior caps fall back to opaque (no dissolve).");
                return MakeLit(tint, smoothness: 0.05f);
            }

            var mat = new Material(shader);
            mat.name = $"Interior_{ColorUtility.ToHtmlStringRGB(tint)}";
            mat.SetColor("_BaseColor", tint);
            mat.SetTexture("_BaseMap", Texture2D.whiteTexture);
            mat.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));
            ConfigureFractureTransparent(mat);
            return mat;
        }

        // ── URP/Lit runtime configuration — transparent + Cull Off ─────────
        //
        // URP/Lit's editor inspector calls `BaseShaderGUI.SetupMaterialBlend
        // ModeAndDepthWriteAndDepthTest` whenever you change the Surface
        // dropdown; that helper writes both the float properties AND the
        // shader keywords + render queue. None of that fires for runtime-
        // created materials, so we replicate it explicitly here.
        //
        // What the build looks like in the inspector after we're done:
        //   Surface     = Transparent
        //   Blend       = Alpha
        //   Cull        = Off
        //   Z-Write     = ON
        //   Render queue= Transparent (3000)
        //   Keyword     _SURFACE_TYPE_TRANSPARENT enabled
        //
        // ZWrite ON on a transparent surface is non-standard but load-
        // bearing for the chunk-debris look. With Cull Off both faces of a
        // fragment rasterize, and ZWrite Off would let "whichever triangle
        // the GPU happened to draw last" win the pixel — back-face lighting
        // shows about half the time, presenting as the "see-through to
        // interior" symptom on solid chunks. ZWrite On means the depth
        // test selects the CLOSER face regardless of rasterization order,
        // so the user always sees the outward-facing side. Trade-off: a
        // fragment behind a closer fragment during the overlap of fade-
        // outs gets z-occluded instead of alpha-mixing through it —
        // acceptable for chunk debris that's spread out on screen.
        static void ConfigureFractureTransparent(Material mat)
        {
            // Surface / blend mode floats — drive URP/Lit's per-pass blend
            // state.
            mat.SetFloat("_Surface",   1f);   // Transparent
            mat.SetFloat("_Blend",     0f);   // Alpha (vs. Premultiply / Additive / Multiply)
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_Cull",      0f);   // Off — both faces visible
            mat.SetFloat("_SrcBlend",  (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend",  (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite",    1f);   // see comment above for why this is ON

            // Keyword + render-queue + tag. Without these URP renders the
            // material in the opaque pass (queue 2000), which produces the
            // "transparent material rendered with an opaque alpha test"
            // symptom — fragments either look fully solid all the way to
            // alpha=0 (alpha-test) or are placed in the wrong z-sorting
            // bucket relative to the rest of the transparent queue.
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // ── Internals ──────────────────────────────────────────────────────

        static Material MakeLit(Color color, float smoothness)
        {
            var shader = Shader.Find(LIT_SHADER) ?? Shader.Find(SIMPLE_LIT);
            if (shader == null)
            {
                Debug.LogError($"[MeshFractureDemo] URP Lit shader not found ({LIT_SHADER} or {SIMPLE_LIT}). Confirm com.unity.render-pipelines.universal is installed and the active pipeline.");
                shader = Shader.Find("Standard");
            }
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))     mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            return mat;
        }
    }
}
