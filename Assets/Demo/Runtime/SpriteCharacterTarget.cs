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

            Vector2 centerUV = new Vector2(0.5f, 0.5f);
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

            // Front (-Z normal). Wound CCW when viewed from -Z.
            AddFace(v_FrontBR, v_FrontBL, v_FrontTL, v_FrontTR, -Vector3.forward,
                    uv10, uv00, uv01, uv11);

            // Back (+Z normal). U mirrored so the sprite isn't reverse-text on the back.
            AddFace(v_BackBL, v_BackBR, v_BackTR, v_BackTL, Vector3.forward,
                    uv00, uv10, uv11, uv01);

            // Top / bottom / right / left (cap-style faces with neutral UV).
            AddFace(v_FrontTL, v_BackTL, v_BackTR, v_FrontTR, Vector3.up,    centerUV, centerUV, centerUV, centerUV);
            AddFace(v_FrontBR, v_BackBR, v_BackBL, v_FrontBL, Vector3.down,  centerUV, centerUV, centerUV, centerUV);
            AddFace(v_FrontBR, v_FrontTR, v_BackTR, v_BackBR, Vector3.right, centerUV, centerUV, centerUV, centerUV);
            AddFace(v_BackBL,  v_BackTL,  v_FrontTL, v_FrontBL, Vector3.left, centerUV, centerUV, centerUV, centerUV);

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
