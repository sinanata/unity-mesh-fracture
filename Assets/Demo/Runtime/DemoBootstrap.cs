using System.Collections.Generic;
using System.Runtime.InteropServices;
using MeshFracture;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MeshFractureDemo
{
    /// <summary>
    /// Single MonoBehaviour that spawns the entire demo: ground plane, lighting,
    /// camera, post-FX volume, demo targets in a circle, and the UI overlay.
    /// Drop on an empty GameObject in <c>MeshFractureDemo.unity</c>; everything
    /// else is built at runtime from prefabs and Resources.
    ///
    /// Mirrors the unity-ui-document-design-system pattern: hand-authored scene
    /// stays trivially small (one component reference, no GUID minefield) and
    /// the rest is C# so refactors don't rot scene YAML.
    /// </summary>
    public class DemoBootstrap : MonoBehaviour
    {
        // Spawn ourselves on every scene load — keeps the demo .unity file
        // empty of GameObject references (no script GUID to chase across
        // refactors). If a contributor places a DemoBootstrap manually in
        // their scene, the Find guard skips the auto-spawn.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoSpawn()
        {
#if UNITY_2023_1_OR_NEWER
            var existing = Object.FindFirstObjectByType<DemoBootstrap>();
#else
            var existing = Object.FindObjectOfType<DemoBootstrap>();
#endif
            if (existing != null) return;
            var go = new GameObject("DemoBootstrap");
            go.AddComponent<DemoBootstrap>();
        }

        // ── Layout ─────────────────────────────────────────────────────────
        // Circle radius is derived per-build from the largest target
        // footprint; this constant is the floor the demo never goes below
        // (so a scene full of small primitives still feels like a stage,
        // not a huddle). Pedestals likewise size themselves to each
        // object's footprint with a small overhang — no more 1-unit
        // pedestals under 2.6-unit crates.
        const float MIN_CIRCLE_RADIUS = 4.5f;
        const float NEIGHBOUR_GAP     = 0.8f;   // chord-clearance between adjacent pedestals
        const float PEDESTAL_HEIGHT   = 0.4f;
        const float PEDESTAL_PADDING  = 0.4f;   // total overhang (0.2u each side past the model footprint)
        const float MIN_PEDESTAL_SIZE = 1.0f;
        const float CAMERA_DISTANCE   = 11f;
        const float CAMERA_HEIGHT     = 5.5f;
        const float CAMERA_FOV        = 45f;

        // Zoom feel — wheel + pinch write to a TARGET distance and an Update
        // pass eases the live distance toward it via SmoothDamp. Sensitivities
        // are tuned so a single Windows wheel-notch covers most of the zoom
        // range (with smoothing easing the camera there) and a comfortable
        // pinch on a phone covers it in 1–2 thumb sweeps.
        const float WHEEL_SENSITIVITY     = 12f;       // distance units per normalized scroll unit
        const float WHEEL_NORMALIZE_DIV   = 100f;      // raw → normalized (Windows wheel ≈ 120/notch)
        const float WHEEL_NORMALIZE_CLAMP = 3f;        // saturate huge scroll spikes (some browsers fire 100s)
        const float PINCH_SENSITIVITY     = 1f / 8f;   // distance units per buffer-pixel of pinch delta
        const float ZOOM_SMOOTH_TIME      = 0.12f;
        const float MIN_ZOOM              = 5f;
        const float MAX_ZOOM              = 24f;

        // ── State ──────────────────────────────────────────────────────────
        Camera _camera;
        Transform _cameraPivot;
        readonly List<DemoTarget> _targets = new();
        DemoUI _ui;

        // Camera control state — orbital pivot driven by drag (mouse or
        // touch) and pinch/wheel zoom. Plain MonoBehaviour, no Cinemachine
        // dependency to keep the demo project as small as the README claims.
        float _orbitYaw   = 30f;   // around Y
        float _orbitPitch = 20f;   // around X (looking down)
        float _orbitDistance;      // current (smoothed) camera distance
        float _targetOrbitDistance;// destination distance written by zoom input
        float _orbitDistanceVelocity; // SmoothDamp scratch

        // Unified tap-vs-drag for the primary pointer. On touch devices the
        // "right-button to orbit" pattern doesn't work — there's only one
        // finger. So a primary-button gesture starts as a candidate tap
        // (sub-threshold movement → fracture on release) and promotes to a
        // drag (orbit) once movement exceeds the threshold. Cleanly handles
        // mouse + touch with no platform branches.
        //
        // Threshold is expressed in CSS-equivalent pixels and scaled by the
        // effective DPR at runtime — Pointer.current.position reports buffer
        // pixels (Screen.width on WebGL with matchWebGLToCanvasSize is the
        // buffer count), so an 11 CSS-px threshold becomes 33 buffer-px on
        // an iPhone-class DPR-3 device. A flat 12 buffer-px threshold would
        // promote a 4 CSS-px finger jiggle to "drag" — roughly the size of
        // resting-finger noise — and the tap would never fire.
        const float DRAG_THRESHOLD_CSSPX = 11f;
        bool       _primaryActive;
        Vector2    _primaryDownPos;
        Vector2    _primaryLastPos;
        bool       _primaryDragging;
        DemoTarget _primaryCandidate;          // target hit at down-time, fractured on release if tap

        // Right/middle drag — desktop power-user shortcut. Skips the tap
        // arbitration so a quick right-click flick still orbits cleanly.
        bool    _altDragging;
        Vector2 _altLastMouse;

        // Two-finger pinch (touch only). Tracks delta between consecutive
        // frames; touchCount drops to <2 → pinch session ends.
        bool  _pinching;
        float _lastPinchDist;

        // ── Settings the UI mutates ────────────────────────────────────────
        public int  FragmentCount = 8;
        public bool TrailsEnabled = true;
        public float ExplosionForce = 18f;

        void Start()
        {
            BuildEnvironment();
            BuildCamera();
            BuildTargets();
            BuildUI();
        }

        // =================================================================
        // Scene construction
        // =================================================================

        void BuildEnvironment()
        {
            // ── Sun ─────────────────────────────────────────────────────────
            var sunGO = new GameObject("Sun");
            sunGO.transform.SetParent(transform, false);
            sunGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var sun = sunGO.AddComponent<Light>();
            sun.type      = LightType.Directional;
            sun.color     = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.6f;
            sun.shadows   = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            sunGO.AddComponent<UniversalAdditionalLightData>();

            // ── Ground ──────────────────────────────────────────────────────
            // Big quad with a subtle radial fade so the demo reads as "stage"
            // without the eye getting pulled to the horizon. Material is built
            // programmatically — no .asset to ship, no GUID to maintain.
            var groundGO = new GameObject("Ground");
            groundGO.transform.SetParent(transform, false);
            groundGO.transform.position = new Vector3(0f, -PEDESTAL_HEIGHT * 0.5f - 0.001f, 0f);
            groundGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            groundGO.transform.localScale = new Vector3(40f, 40f, 1f);
            var mf = groundGO.AddComponent<MeshFilter>();
            mf.sharedMesh = BuildGroundMesh();
            var mr = groundGO.AddComponent<MeshRenderer>();
            mr.sharedMaterial = MaterialFactory.GroundMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows    = true;

            // Separate flat-box collider for the ground — the rendering quad
            // is a single MeshFilter triangle with no thickness, useless
            // for physics. The collider sits at the same Y as the ground
            // visual but extends 1 unit down into the world so fragments
            // can never tunnel through during a fast bounce.
            var groundColGO = new GameObject("GroundCollider");
            groundColGO.transform.SetParent(transform, false);
            groundColGO.transform.position = groundGO.transform.position
                + new Vector3(0f, -0.5f, 0f);
            var groundCol = groundColGO.AddComponent<BoxCollider>();
            groundCol.size = new Vector3(40f, 1f, 40f);

            // ── Global volume ──────────────────────────────────────────────
            var volumeGO = new GameObject("Global Volume");
            volumeGO.transform.SetParent(transform, false);
            var vol = volumeGO.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 0;
            // The default volume profile is at Assets/DefaultVolumeProfile.asset;
            // not in Resources — we leave it null and let URP's default profile
            // apply. Visitors can drop in their own profile by editing the
            // Volume in the scene.
        }

        void BuildCamera()
        {
            var camGO = new GameObject("Main Camera");
            camGO.tag = "MainCamera";
            camGO.transform.SetParent(transform, false);
            _camera = camGO.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.043f, 0.058f, 0.090f, 1f); // matches WebGL template + UI bg
            _camera.fieldOfView = CAMERA_FOV;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane  = 100f;
            _camera.allowHDR = true;
            _camera.allowMSAA = true;
            camGO.AddComponent<AudioListener>();

            var urpData = camGO.AddComponent<UniversalAdditionalCameraData>();
            urpData.renderPostProcessing = true;

            // Pivot the camera at world origin so the orbit math is trivial:
            // distance + yaw + pitch determine position; LookAt handles rotation.
            _cameraPivot = camGO.transform;
            _orbitDistance       = CAMERA_DISTANCE;
            _targetOrbitDistance = CAMERA_DISTANCE;
            ApplyCameraOrbit();
        }

        void BuildTargets()
        {
            var defs = DemoTargetCatalog.GetDefinitions();
            int count = defs.Count;

            // Pass 1 — instantiate each target at the parent's origin and
            // measure its world bounds. We need every footprint up-front
            // because the circle radius and the per-object pedestal size
            // are both derived from them.
            var built = new (GameObject go, Bounds bounds, DemoTargetCatalog.Definition def)[count];
            for (int i = 0; i < count; i++)
            {
                var def = defs[i];
                var go = def.Build();
                go.transform.SetParent(transform, false);
                built[i] = (go, ComputeWorldBounds(go), def);
            }

            // Pass 2 — derive the circle radius from the widest object so
            // adjacent pedestals don't crowd each other. Chord between two
            // neighbours on a circle of radius R with N points equals
            // 2·R·sin(π/N); solve for R given a desired chord of
            // (max footprint + neighbour gap).
            float maxFootprint = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3 sz = built[i].bounds.size;
                maxFootprint = Mathf.Max(maxFootprint, Mathf.Max(sz.x, sz.z));
            }
            float requiredChord = maxFootprint + NEIGHBOUR_GAP;
            float radius = Mathf.Max(MIN_CIRCLE_RADIUS,
                requiredChord / (2f * Mathf.Sin(Mathf.PI / count)));

            // Pass 3 — place each target with a pedestal sized to its actual
            // footprint, then attach the click collider AFTER positioning so
            // the local-space AABB calculation reads the post-move transform.
            for (int i = 0; i < count; i++)
            {
                var (targetGO, wb, def) = built[i];

                float angle = (i / (float)count) * Mathf.PI * 2f - Mathf.PI * 0.5f;
                Vector3 anchor = new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius);

                float pedX = Mathf.Max(MIN_PEDESTAL_SIZE, wb.size.x + PEDESTAL_PADDING);
                float pedZ = Mathf.Max(MIN_PEDESTAL_SIZE, wb.size.z + PEDESTAL_PADDING);

                var pedestalGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pedestalGO.name = $"Pedestal_{def.DisplayName}";
                pedestalGO.transform.SetParent(transform, false);
                pedestalGO.transform.position   = anchor + new Vector3(0f, -PEDESTAL_HEIGHT * 0.5f, 0f);
                pedestalGO.transform.localScale = new Vector3(pedX, PEDESTAL_HEIGHT, pedZ);
                var pedestalMR = pedestalGO.GetComponent<MeshRenderer>();
                pedestalMR.sharedMaterial    = MaterialFactory.PedestalMaterial();
                pedestalMR.shadowCastingMode = ShadowCastingMode.On;
                // The auto-added BoxCollider stays — fragments need to
                // bounce off pedestals when UseUnityPhysics is on. Click
                // raycast disambiguates pedestal-vs-target via
                // TryResolveTargetAt's RaycastAll walk.

                // Place the target so the bottom of its bounds rests on the
                // pedestal top (anchor.y == 0). targetGO is still at world
                // origin here, so wb.center.y is in the same frame as the
                // offset we apply.
                Vector3 targetPos = anchor + new Vector3(
                    0f,
                    (wb.size.y * 0.5f) - (wb.center.y - targetGO.transform.position.y),
                    0f);
                targetGO.transform.position = targetPos;

                // Trigger-style raycast collider. Local-space AABB so the
                // collider tracks any future world transforms (including
                // rotation, e.g., the cannon at 90° Y) without offsetting.
                AttachClickCollider(targetGO);

                var target = targetGO.AddComponent<DemoTarget>();
                target.Initialize(def, this);
                _targets.Add(target);
            }

            // Showcase wall: a tall slab placed BEHIND one target (radially
            // outward from origin) so the dramatic explosion-outward
            // direction sends fragments straight into a real collider.
            // Sized + positioned from the dynamic radius so it stays close
            // enough regardless of how big the targets ended up.
            BuildShowcaseWall(radius);
        }

        void BuildShowcaseWall(float circleRadius)
        {
            // Pick a target slot — index 0 sits at angle -π/2 (= -Z direction).
            // Wall placed past that slot, facing the origin, with enough
            // clearance from the pedestal so fragments have a flight arc
            // before impact.
            int targetIndex = 0;
            int slotCount = _targets.Count > 0 ? _targets.Count : 8;
            float angle = (targetIndex / (float)slotCount) * Mathf.PI * 2f - Mathf.PI * 0.5f;
            float wallRadius = circleRadius + 1.5f;
            Vector3 wallPos = new Vector3(
                Mathf.Cos(angle) * wallRadius,
                1.4f,                     // half of wall height — sits on the ground
                Mathf.Sin(angle) * wallRadius);

            var wallGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wallGO.name = "ShowcaseWall";
            wallGO.transform.SetParent(transform, false);
            wallGO.transform.position   = wallPos;
            // Slight inward tilt makes fragments bounce DOWN-and-back instead
            // of straight up, which reads better on camera. The wall faces
            // toward the origin (arctangent of its position from origin).
            wallGO.transform.rotation   = Quaternion.LookRotation(-wallPos.normalized) * Quaternion.Euler(-8f, 0f, 0f);
            wallGO.transform.localScale = new Vector3(4.5f, 2.8f, 0.35f);

            var wallMR = wallGO.GetComponent<MeshRenderer>();
            wallMR.sharedMaterial    = MaterialFactory.WallMaterial();
            wallMR.shadowCastingMode = ShadowCastingMode.On;
            // BoxCollider auto-added by CreatePrimitive — keep it for fragment
            // collisions.
        }

        void BuildUI()
        {
            var uiGO = new GameObject("DemoUI");
            uiGO.transform.SetParent(transform, false);
            _ui = uiGO.AddComponent<DemoUI>();
            _ui.AttachTo(this);
        }

        // =================================================================
        // Update loop — input & camera
        // =================================================================

        void Update()
        {
            // Order matters: pinch first (claims gesture before primary even
            // sees the second touch), then primary tap-or-drag (mobile +
            // desktop), then right/middle drag (desktop power user), then
            // wheel zoom (desktop only). All handlers write to camera state
            // fields; the single ApplyCameraOrbit call at the bottom of the
            // tick pushes everything to the transform — drag updates orbit
            // angles 1:1, while zoom eases _orbitDistance toward
            // _targetOrbitDistance via SmoothDamp.
            HandlePinchZoom();
            HandlePrimaryPointer();
            HandleAltMouseDrag();
            HandleWheelZoom();
            HandleHotkeys();

            _orbitDistance = Mathf.SmoothDamp(_orbitDistance, _targetOrbitDistance,
                ref _orbitDistanceVelocity, ZOOM_SMOOTH_TIME);
            ApplyCameraOrbit();
        }

        // ─── Primary pointer (left mouse OR single touch) ──────────────────
        // Unified handler so tapping on mobile fractures, dragging on mobile
        // orbits, and desktop users get the same. The trick is the
        // DRAG_THRESHOLD arbitration: until movement exceeds it, the gesture
        // is a candidate tap. Once it does, the gesture becomes a drag and
        // the candidate is forgotten so release won't accidentally fracture.
        void HandlePrimaryPointer()
        {
            // While a 2-finger pinch is active, swallow the primary handler —
            // the second finger registered as a Down event we don't want to
            // resolve as a tap.
            if (_pinching) { _primaryActive = false; return; }

            // Pointer.current dispatches to mouse on desktop and touchscreen
            // primary touch on mobile, so the same code path handles both
            // without platform branches.
            var pointer = Pointer.current;
            if (pointer == null) return;

            bool down = pointer.press.wasPressedThisFrame;
            bool held = pointer.press.isPressed;
            bool up   = pointer.press.wasReleasedThisFrame;

            if (down)
            {
                // Tap on UI: don't even start a gesture. We use the live
                // PointerOverUI flag from DemoUI (driven by UI Toolkit's
                // PointerEnter/Leave events on the panel surfaces).
                if (_ui != null && _ui.PointerOverUI) { _primaryActive = false; return; }
                if (_camera == null) return;

                _primaryActive    = true;
                _primaryDragging  = false;
                _primaryDownPos   = pointer.position.ReadValue();
                _primaryLastPos   = _primaryDownPos;
                // Skip target-candidate resolution while pre-bakes are still
                // working through their queue. Drag-to-orbit still functions
                // (the gesture-arbitration code below doesn't depend on the
                // candidate), but a tap-release won't fire Fracture() against
                // a target whose fragments aren't cached yet — which would
                // either freeze the main thread on a sync bake or no-op
                // confusingly. The DemoUI surface communicates the same state
                // by disabling the Fracture-All button + spinning a refresh
                // glyph in its place.
                _primaryCandidate = AreAllBakesReady() ? TryResolveTargetAt(_primaryDownPos) : null;
            }

            if (_primaryActive && held)
            {
                Vector2 cur = pointer.position.ReadValue();
                if (!_primaryDragging)
                {
                    // Promote candidate-tap to drag once total movement
                    // crosses the threshold. Read the FULL displacement
                    // from down position, not the per-frame delta — small
                    // shaky movements would otherwise never accumulate.
                    float thresholdPx = DRAG_THRESHOLD_CSSPX * GetInputDprScale();
                    if ((cur - _primaryDownPos).sqrMagnitude > thresholdPx * thresholdPx)
                    {
                        _primaryDragging  = true;
                        _primaryCandidate = null;   // gesture is no longer a tap
                    }
                }

                if (_primaryDragging)
                {
                    Vector2 delta = cur - _primaryLastPos;
                    _orbitYaw   += delta.x * 0.25f;
                    _orbitPitch  = Mathf.Clamp(_orbitPitch - delta.y * 0.2f, 5f, 75f);
                }
                _primaryLastPos = cur;
            }

            if (_primaryActive && up)
            {
                if (!_primaryDragging && _primaryCandidate != null)
                    _primaryCandidate.Fracture();
                _primaryActive    = false;
                _primaryCandidate = null;
                _primaryDragging  = false;
            }
        }

        // ─── Right / middle mouse drag (desktop only) ──────────────────────
        void HandleAltMouseDrag()
        {
            var mouse = Mouse.current;
            if (mouse == null) { _altDragging = false; return; }

            bool downAlt = mouse.rightButton.wasPressedThisFrame  || mouse.middleButton.wasPressedThisFrame;
            bool upAlt   = mouse.rightButton.wasReleasedThisFrame || mouse.middleButton.wasReleasedThisFrame;
            Vector2 mp   = mouse.position.ReadValue();

            if (downAlt) { _altDragging = true; _altLastMouse = mp; }
            if (upAlt)     _altDragging = false;

            if (_altDragging)
            {
                Vector2 delta = mp - _altLastMouse;
                _altLastMouse = mp;
                _orbitYaw   += delta.x * 0.25f;
                _orbitPitch  = Mathf.Clamp(_orbitPitch - delta.y * 0.2f, 5f, 75f);
            }
        }

        // ─── Two-finger pinch zoom (touch) ─────────────────────────────────
        // The browser's gesturestart/gesturechange events are pre-empted by
        // the WebGL template's preventDefault, so Unity sees the raw
        // touchCount==2 state. Distance between touches drives the zoom.
        void HandlePinchZoom()
        {
            var ts = Touchscreen.current;
            if (ts == null) { _pinching = false; return; }

            // Walk the touches array and pluck the first two whose press is
            // currently held. New Input System has no touchCount equivalent —
            // touches[i].press.isPressed is the canonical "active" check.
            TouchControl t0 = null, t1 = null;
            int active = 0;
            foreach (var t in ts.touches)
            {
                if (!t.press.isPressed) continue;
                if      (active == 0) t0 = t;
                else if (active == 1) t1 = t;
                active++;
                if (active >= 2) break;
            }

            if (active >= 2)
            {
                Vector2 p0 = t0.position.ReadValue();
                Vector2 p1 = t1.position.ReadValue();
                float dist = Vector2.Distance(p0, p1);
                if (!_pinching) { _pinching = true; _lastPinchDist = dist; return; }

                // Pinch writes to the TARGET distance and the SmoothDamp pass
                // in Update eases the live distance toward it. Sensitivity is
                // 10× the legacy 1/120 px → 1/12 px so a comfortable thumb
                // stretch covers a meaningful slice of the zoom range, but
                // the easing keeps it from snapping.
                float deltaPx = dist - _lastPinchDist;
                _targetOrbitDistance = Mathf.Clamp(
                    _targetOrbitDistance - deltaPx * PINCH_SENSITIVITY,
                    MIN_ZOOM, MAX_ZOOM);
                _lastPinchDist = dist;
            }
            else
            {
                _pinching = false;
            }
        }

        void HandleWheelZoom()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            // InputSystem reports raw scroll units, and the magnitude varies
            // wildly across platforms:
            //   - Windows mouse wheel: ~120 per notch (WM_MOUSEWHEEL convention)
            //   - macOS / trackpad / WebGL smooth-scroll: ~1–10 per event
            //   - Some browsers fire huge spikes (300+) on momentum scroll
            // The legacy "/120f" normalization made trackpads feel dead because
            // a 5-unit smooth scroll became 0.04 effective input. Instead,
            // divide by a smaller constant so smooth scrolls register, then
            // clamp the result so a single Windows notch (~1.2 normalized) or
            // a momentum spike both saturate at WHEEL_NORMALIZE_CLAMP. With
            // WHEEL_SENSITIVITY=12, a saturated tick = 36 distance units —
            // more than the entire 19-unit zoom range, so smoothing decides
            // how fast the camera arrives, not the input does.
            float raw = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(raw) < 0.001f) return;
            float scroll = Mathf.Clamp(raw / WHEEL_NORMALIZE_DIV,
                -WHEEL_NORMALIZE_CLAMP, WHEEL_NORMALIZE_CLAMP);
            _targetOrbitDistance = Mathf.Clamp(
                _targetOrbitDistance - scroll * WHEEL_SENSITIVITY,
                MIN_ZOOM, MAX_ZOOM);
        }

        DemoTarget TryResolveTargetAt(Vector2 screenPos)
        {
            if (_camera == null) return null;
            Ray ray = _camera.ScreenPointToRay(screenPos);
            // RaycastAll + walk-by-distance: ground / pedestals / wall now
            // have colliders too (so physics-mode fragments can bounce off
            // them), and any of those can sit between camera and target on
            // a glancing click. Walking the sorted hits and returning the
            // FIRST collider that resolves to a DemoTarget skips past the
            // scenery without changing the click feel — the target is
            // almost always the closest hit anyway.
            var hits = Physics.RaycastAll(ray, 100f);
            if (hits == null || hits.Length == 0) return null;
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                var dt = hits[i].collider.GetComponentInParent<DemoTarget>();
                if (dt != null) return dt;
            }
            return null;
        }

        // Effective input-coordinate-to-CSS-pixel scale. Pointer.current.position
        // reports BUFFER pixels (cssWidth × DPR on WebGL with
        // matchWebGLToCanvasSize=true). Returns 1 on standalone desktop,
        // ~2 on Retina laptops, ~3 on iPhone-class WebGL clients. Used to
        // keep CSS-px thresholds (drag arbitration, touch slop) consistent
        // across devices.
        //
        // WebGL path goes through the same jslib as DemoUI so the two
        // pieces agree on the DPR — Screen.dpi can disagree with
        // window.devicePixelRatio on some browsers.
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern float MeshFractureDemo_GetDevicePixelRatio();
#endif
        static float GetInputDprScale()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                float dpr = MeshFractureDemo_GetDevicePixelRatio();
                if (dpr > 0f) return dpr;
            }
            catch { /* fall through to Screen.dpi heuristic */ }
#endif
            float dpi = Screen.dpi;
            if (dpi <= 0f) return 1f;
            return Mathf.Max(1f, dpi / 96f);
        }

        void HandleHotkeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            // Space-fracture is gated on bake readiness for the same reason
            // tap-fracture is — see the candidate-resolution comment above.
            // R-restore and the 1/2/3 fragment-count presets remain unguarded:
            // restore is always safe, and the count presets just enqueue a
            // fresh bake (the slider does the same).
            if (kb.spaceKey.wasPressedThisFrame && AreAllBakesReady()) FractureAll();
            if (kb.rKey.wasPressedThisFrame)      RestoreAll();
            if (kb.digit1Key.wasPressedThisFrame) SetFragmentCount(3);
            if (kb.digit2Key.wasPressedThisFrame) SetFragmentCount(8);
            if (kb.digit3Key.wasPressedThisFrame) SetFragmentCount(16);
        }

        void ApplyCameraOrbit()
        {
            if (_camera == null) return;
            // Convert orbit angles into a position on a sphere of `_orbitDistance`
            // radius around the world origin. Yaw rotates around Y (horizontal
            // sweep), pitch tilts the view downward.
            float yawRad   = _orbitYaw   * Mathf.Deg2Rad;
            float pitchRad = _orbitPitch * Mathf.Deg2Rad;
            float horiz = Mathf.Cos(pitchRad) * _orbitDistance;
            Vector3 pos = new Vector3(
                Mathf.Sin(yawRad) * horiz,
                Mathf.Sin(pitchRad) * _orbitDistance,
                Mathf.Cos(yawRad) * horiz);
            _camera.transform.position = new Vector3(pos.x, Mathf.Max(pos.y, 1.5f), pos.z);
            _camera.transform.LookAt(new Vector3(0f, CAMERA_HEIGHT * 0.4f, 0f));
        }

        // =================================================================
        // UI hooks
        // =================================================================

        public void FractureAll()
        {
            // Coroutine path so we never block the main thread — see
            // FractureAllSpread for the "wait for pre-bakes, then stagger
            // burst spawns one per frame" sequence.
            StartCoroutine(FractureAllSpread());
        }

        System.Collections.IEnumerator FractureAllSpread()
        {
            // Wait until every target's bake is in FragmentCache. The
            // pre-bake is kicked off at BuildTargets time (each
            // DemoTarget.Initialize calls FragmentCache.RequestPreBake)
            // and the worker yields one bake per frame; for an 8-target
            // circle that's typically ready within ~10 frames of scene
            // load. The wait is responsive — no dropped frames — and
            // replaces the previous worst case where clicking Fracture
            // All before pre-bakes finished triggered eight ~60ms
            // synchronous BakeSync calls back-to-back, the 0.5-second
            // freeze the user reported.
            while (!AreAllBakesReady())
                yield return null;

            // Stagger burst spawns and serialise their progressive-build
            // phase: each FractureBurst now spawns its 8 fragments across
            // ~4 Update ticks (BUILD_BATCH_SIZE per frame), so kicking off
            // the next target before the current burst finishes building
            // stacks per-frame work and reintroduces the jitter we were
            // here to fix. Waiting on IsBurstStillBuilding keeps each
            // frame's allocation budget bounded to one burst's batch.
            // The total cascade duration becomes a chain reaction the
            // viewer can actually see, not an invisible 500 ms freeze.
            for (int i = 0; i < _targets.Count; i++)
            {
                if (!_targets[i].IsFractured) _targets[i].Fracture();
                // Yield once before polling so the burst we just started
                // has a frame to do its first BuildOneFragment batch
                // before we measure IsBurstStillBuilding.
                yield return null;
                while (_targets[i].IsBurstStillBuilding) yield return null;
            }
        }

        bool AreAllBakesReady()
        {
            for (int i = 0; i < _targets.Count; i++)
                if (!_targets[i].IsBakeReady) return false;
            return true;
        }

        /// <summary>
        /// True while at least one target's Voronoi pre-bake is still pending.
        /// Drives the DemoUI's "Baking..." button-loading state — disabling
        /// the Fracture All button and spinning the .ds-icon--refresh glyph
        /// in its place — and gates tap/Space fracture inputs so the user
        /// can't trigger a fracture against a target whose fragments aren't
        /// in FragmentCache yet.
        /// </summary>
        public bool IsBaking => !AreAllBakesReady();

        public void RestoreAll()
        {
            foreach (var t in _targets) t.Restore();
        }

        public void SetFragmentCount(int count)
        {
            FragmentCount = Mathf.Clamp(count, 2, 24);
            foreach (var t in _targets) t.OnFragmentCountChanged();
        }

        public void SetTrailsEnabled(bool enabled) { TrailsEnabled = enabled; }
        public void SetExplosionForce(float force) { ExplosionForce = Mathf.Clamp(force, 1f, 60f); }

        public IReadOnlyList<DemoTarget> Targets => _targets;

        // =================================================================
        // Helpers
        // =================================================================

        static Bounds ComputeWorldBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        static void AttachClickCollider(GameObject root)
        {
            // Build a local-space AABB by sampling each child renderer's eight
            // world-space corners and transforming them back into root-local.
            // BoxCollider stores center+size in local space, so doing the
            // collapse to local up-front means the collider follows the root
            // through any future world transform — including the rotated
            // models like the 90°-Y cannon, where a naive
            // InverseTransformVector(worldBounds.size) silently shrank the
            // collider along the wrong axis.
            //
            // The previous version was also fed the PRE-move world bounds
            // and then ran InverseTransformPoint through the POST-move
            // transform, leaving every collider offset by the move vector.
            // This rewrite reads each renderer's CURRENT world bounds, so
            // it works whether called before or after positioning.
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var box = root.AddComponent<BoxCollider>();
            box.isTrigger = false;

            if (renderers.Length == 0)
            {
                box.center = Vector3.zero;
                box.size   = Vector3.one;
                return;
            }

            var rootT = root.transform;
            Bounds local = default;
            bool init = false;
            foreach (var r in renderers)
            {
                Bounds wb = r.bounds;
                Vector3 c = wb.center, e = wb.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 worldCorner = c + new Vector3(sx * e.x, sy * e.y, sz * e.z);
                    Vector3 localCorner = rootT.InverseTransformPoint(worldCorner);
                    if (!init) { local = new Bounds(localCorner, Vector3.zero); init = true; }
                    else local.Encapsulate(localCorner);
                }
            }
            box.center = local.center;
            box.size   = local.size;
        }

        static Mesh BuildGroundMesh()
        {
            // Simple 1x1 quad (XY plane). Scaled by the ground GameObject.
            var mesh = new Mesh { name = "DemoGround" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
            };
            mesh.normals = new[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
            mesh.uv = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
