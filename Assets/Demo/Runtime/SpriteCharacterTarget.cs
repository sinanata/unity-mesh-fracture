using MeshFracture;
using SpriteBaker;
using UnityEngine;

namespace MeshFractureDemo
{
    /// <summary>
    /// Sprite-rendered character that fractures into 2D pieces — showcases
    /// the joint pipeline: <c>unity-3d-to-sprite-baker</c> bakes the Kenney
    /// AC2 character (different skin per instance) into a sprite atlas at
    /// scene start; <c>unity-mesh-fracture</c> Voronoi-cuts a thin-box mesh
    /// whose front face carries the atlas UVs, so each fragment shows a
    /// piece of the original sprite when the burst spawns.
    ///
    /// Bottom-aligned to the GameObject pivot (matches sprite-baker's
    /// AnimatedSpriteRenderer convention) so swapping between live 3D and
    /// the sprite playback at the same world position doesn't move feet.
    ///
    /// Billboarding: <see cref="LateUpdate"/> rotates the GameObject around
    /// world-Y so the box's front face (-Z normal) always points at the
    /// orbit camera. Without this, the user sees the BACK face when the
    /// camera arcs to the far side, and the back face's mirrored sprite
    /// reads as "the character is facing away from you". Y-axis only
    /// keeps the sprite vertical — pitch/roll would tip a standing
    /// character onto its back as the camera tilts.
    ///
    /// Border-leak fix: the thin-box's side + back faces previously
    /// sampled the atlas center (0.5, 0.5) — picks up the character's
    /// torso color and frames the silhouette with a colored band when the
    /// camera tumbles through the box's edge. UVs (0, 0) map to the
    /// transparent-padding corner of the atlas; alpha-clip discards
    /// every side / back fragment, leaving the front face as the only
    /// visible surface.
    ///
    /// Sprite-mode burst: <see cref="ConfigureBurst"/> hands the burst a
    /// camera-perpendicular plane normal, switches it to CPU sim, and
    /// caps gravity so fragments drift sideways in the sprite plane
    /// rather than falling out of frame in 3D.
    /// </summary>
    public class SpriteCharacterTarget : MonoBehaviour
    {
        // Card dimensions — sized to match what SpriteAtlasBaker produces
        // for a Kenney AC2 character at scale 1.3 with default 15% padding.
        // Single bind-pose row → atlas is 1 frame × 1 row, so the front
        // face's UV (0,0)→(1,1) covers the whole frame. If the actual atlas
        // QuadWidth/Height comes back different (e.g. user retunes scale),
        // RebuildMeshFor resizes the mesh in Update.
        const float DEFAULT_CARD_WIDTH  = 3.6f;
        const float DEFAULT_CARD_HEIGHT = 3.6f;
        const float CARD_DEPTH          = 0.12f;

        public Texture2D Skin;
        public string DisplayName;
        public bool IsAtlasReady => _atlasReady;

        GameObject _capturePrefab;
        int _bakeKey;
        bool _atlasReady;
        Mesh _quadMesh;
        MeshRenderer _meshRenderer;
        Material _placeholderMat;
        float _currentCardWidth, _currentCardHeight;
        Camera _cameraCache;

        public void Setup(GameObject capturePrefab, Texture2D skin, string displayName, int bakeKeySeed)
        {
            _capturePrefab = capturePrefab;
            Skin           = skin;
            DisplayName    = displayName;
            _bakeKey       = bakeKeySeed;

            BuildPlaceholderRenderer();
            QueueAtlasBake();
        }

        void BuildPlaceholderRenderer()
        {
            _currentCardWidth  = DEFAULT_CARD_WIDTH;
            _currentCardHeight = DEFAULT_CARD_HEIGHT;
            _quadMesh = BuildThinBoxMesh(_currentCardWidth, _currentCardHeight, CARD_DEPTH);

            var mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = _quadMesh;

            _meshRenderer = gameObject.AddComponent<MeshRenderer>();
            _placeholderMat = MaterialFactory.SpritePlaceholderMaterial();
            _meshRenderer.sharedMaterial    = _placeholderMat;
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows    = false;
        }

        void QueueAtlasBake()
        {
            if (_capturePrefab == null) return;

            SpriteAtlasBaker.Instance.Enqueue(new SpriteBakeRequest
            {
                Key             = _bakeKey,
                Prefab          = _capturePrefab,
                Rows            = new[] { new SpriteAnimRow { Row = 0, ClipName = "", SingleFrame = true } },
                FramePixelSize  = 256,
                FrameRate       = 1,
                // Kenney AC2 prefab is authored facing -Z; rotate 180° so the atlas
                // captures the FRONT of the character on the textured face.
                CaptureRotation = Quaternion.Euler(0f, 180f, 0f),
                Lighting        = CaptureLighting.Default,
                BackgroundColor = Color.clear,
                PreCaptureCallback = ApplySkin,
            });
        }

        void ApplySkin(GameObject inst)
        {
            if (Skin == null) return;
            var mat = MaterialFactory.CharacterSkinMaterial(Skin);
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mats = new Material[smr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                smr.sharedMaterials = mats;
            }
            foreach (var mr in inst.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = new Material[mr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                mr.sharedMaterials = mats;
            }
        }

        void Update()
        {
            if (_atlasReady) return;
            if (!SpriteAtlasCache.TryGet(_bakeKey, out var atlas)) return;

            // Atlas just bound. Resize mesh if the baker's actual quad
            // dimensions differ from our placeholder, then swap material.
            bool meshChanged = false;
            if (!Mathf.Approximately(atlas.QuadWidth,  _currentCardWidth) ||
                !Mathf.Approximately(atlas.QuadHeight, _currentCardHeight))
            {
                RebuildMeshFor(atlas.QuadWidth, atlas.QuadHeight);
                meshChanged = true;
            }

            _meshRenderer.sharedMaterial = atlas.SharedMaterial;
            _atlasReady = true;

            // If DemoTarget already kicked off a fragment pre-bake against
            // the placeholder mesh size, evict + re-bake against the
            // atlas-sized mesh — otherwise spawned fragments would be
            // ~1% smaller / larger than the rendered sprite quad.
            if (meshChanged)
            {
                var demoTarget = GetComponent<DemoTarget>();
                if (demoTarget != null) demoTarget.OnFragmentCountChanged();
            }
        }

        /// <summary>
        /// Y-axis billboard: rotate the sprite mesh so its front face
        /// (the textured -Z plane built by <see cref="BuildThinBoxMesh"/>)
        /// always points at the camera, regardless of orbit angle. Y-only
        /// keeps the character standing upright; a full 3-axis billboard
        /// would tilt it onto its back when the camera looks down. Runs
        /// in LateUpdate so it sees the camera's final transform for the
        /// frame after DemoBootstrap's orbit pass.
        /// </summary>
        void LateUpdate()
        {
            if (_cameraCache == null) _cameraCache = Camera.main;
            if (_cameraCache == null) return;
            Vector3 toCam = _cameraCache.transform.position - transform.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-4f) return;
            transform.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
        }

        /// <summary>
        /// Apply sprite-burst settings to a <see cref="FractureBurst"/>
        /// after <see cref="DemoTarget"/> has constructed it. Switches the
        /// burst to CPU simulation (so the plane-lock applies — Unity
        /// physics has no per-Rigidbody plane constraint) and tells it to
        /// project velocity onto the camera-perpendicular plane that the
        /// sprite is facing at burst time. Result: fragments stay in the
        /// flat sprite plane, drift sideways within it under reduced
        /// gravity, and never travel toward / away from the camera.
        /// </summary>
        public void ConfigureBurst(FractureBurst burst)
        {
            if (burst == null) return;
            var cam = _cameraCache != null ? _cameraCache : Camera.main;
            if (cam == null) return;

            // Plane normal = camera forward at burst time. Captured as a
            // world vector so subsequent camera orbits don't change the
            // plane the fragments are constrained to (they belong to the
            // sprite frozen at the moment of fracture, not a rotating
            // billboard plane).
            burst.LockPlaneNormal = cam.transform.forward;

            // CPU sim path so LockPlaneNormal is honoured. UseUnityPhysics
            // mode would let fragments drift along the normal under
            // collision impulses + gravity — Rigidbody doesn't ship a
            // per-axis or per-plane constraint that maps cleanly to an
            // arbitrary world plane.
            burst.UseUnityPhysics = false;

            // Lighter gravity than the full 3D demo: at -25 the fragments
            // dive off the bottom of the sprite plane fast, leaving the
            // billboard empty almost immediately. -8 reads as "drifting
            // pieces of paper" which suits the 2D look.
            burst.GravityVector = new Vector3(0f, -8f, 0f);
        }

        void RebuildMeshFor(float width, float height)
        {
            var fresh = BuildThinBoxMesh(width, height, CARD_DEPTH);
            var mf = GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = fresh;
            if (_quadMesh != null) Destroy(_quadMesh);
            _quadMesh = fresh;
            _currentCardWidth  = width;
            _currentCardHeight = height;
        }

        void OnDestroy()
        {
            if (_quadMesh       != null) Destroy(_quadMesh);
            if (_placeholderMat != null) Destroy(_placeholderMat);
        }

        // 24-vertex flat-shaded thin box, bottom-center pivot. Front face is
        // at -Z (matches sprite-baker's AnimatedSpriteRenderer convention),
        // back at +Z. Front + back UVs span (0,0)→(1,1), so for a single-
        // frame bind-pose atlas they sample the whole captured silhouette.
        // Side faces get a centred UV so cap pixels sample near the
        // sprite's average colour after fracture.
        static Mesh BuildThinBoxMesh(float width, float height, float depth)
        {
            float hw = width  * 0.5f;
            float ht = depth  * 0.5f;

            Vector3 v_FrontBL = new Vector3(-hw, 0,      -ht);
            Vector3 v_FrontBR = new Vector3( hw, 0,      -ht);
            Vector3 v_FrontTR = new Vector3( hw, height, -ht);
            Vector3 v_FrontTL = new Vector3(-hw, height, -ht);
            Vector3 v_BackBL  = new Vector3(-hw, 0,       ht);
            Vector3 v_BackBR  = new Vector3( hw, 0,       ht);
            Vector3 v_BackTR  = new Vector3( hw, height,  ht);
            Vector3 v_BackTL  = new Vector3(-hw, height,  ht);

            // Front face uses the full sprite frame; every OTHER face
            // samples the bottom-left corner (0, 0) of the atlas. The
            // sprite is centered with transparent padding around it, so
            // (0, 0) is reliably alpha=0 — alpha-clip then discards every
            // back/side fragment. Without this, the box's side faces
            // sampled the atlas center (the character's torso color) and
            // showed as a colored frame around the silhouette when the
            // camera angle revealed an edge; the back face showed the
            // sprite mirrored, reading as "facing the wrong way" when
            // fragments tumbled.
            Vector2 transparentUV = new Vector2(0f, 0f);
            Vector2 uv00 = new Vector2(0f, 0f);
            Vector2 uv10 = new Vector2(1f, 0f);
            Vector2 uv11 = new Vector2(1f, 1f);
            Vector2 uv01 = new Vector2(0f, 1f);

            var verts = new Vector3[24];
            var norms = new Vector3[24];
            var uvs   = new Vector2[24];
            var tris  = new int[36];
            int v = 0, t = 0;

            void AddFace(Vector3 a, Vector3 b1, Vector3 c, Vector3 d, Vector3 n,
                         Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
            {
                verts[v + 0] = a;   verts[v + 1] = b1;  verts[v + 2] = c;   verts[v + 3] = d;
                norms[v + 0] = n;   norms[v + 1] = n;   norms[v + 2] = n;   norms[v + 3] = n;
                uvs  [v + 0] = uvA; uvs  [v + 1] = uvB; uvs  [v + 2] = uvC; uvs  [v + 3] = uvD;
                tris[t + 0] = v + 0; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v + 0; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
                v += 4; t += 6;
            }

            // Front (-Z normal). Wound CCW when viewed from -Z. ONLY this
            // face carries the full sprite UVs — billboarding keeps it
            // pointed at the camera, so it's the only face the user
            // ever sees.
            AddFace(v_FrontBR, v_FrontBL, v_FrontTL, v_FrontTR, -Vector3.forward,
                    uv10, uv00, uv01, uv11);

            // Back + 4 sides all sample the transparent corner (0, 0) so
            // the alpha-clip material discards them. Only matters when
            // fragments tumble (rotation can momentarily expose them) or
            // when the billboard hasn't realigned yet on the first frame
            // after spawn — without this, those frames flash a colored
            // band or a mirror-image sprite.
            AddFace(v_BackBL, v_BackBR, v_BackTR, v_BackTL, Vector3.forward,
                    transparentUV, transparentUV, transparentUV, transparentUV);
            AddFace(v_FrontTL, v_BackTL, v_BackTR, v_FrontTR, Vector3.up,    transparentUV, transparentUV, transparentUV, transparentUV);
            AddFace(v_FrontBR, v_BackBR, v_BackBL, v_FrontBL, Vector3.down,  transparentUV, transparentUV, transparentUV, transparentUV);
            AddFace(v_FrontBR, v_FrontTR, v_BackTR, v_BackBR, Vector3.right, transparentUV, transparentUV, transparentUV, transparentUV);
            AddFace(v_BackBL,  v_BackTL,  v_FrontTL, v_FrontBL, Vector3.left, transparentUV, transparentUV, transparentUV, transparentUV);

            var mesh = new Mesh { name = "SpriteThinBox" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
