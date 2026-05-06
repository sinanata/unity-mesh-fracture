using System.Runtime.InteropServices;
using UIDocumentDesignSystem;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UIElements;

namespace MeshFractureDemo
{
    /// <summary>
    /// UI Toolkit overlay for the demo. Layout is authored in UXML
    /// (Assets/Demo/Resources/UI/DemoUI.uxml) and styled with the ds-*
    /// design system stylesheet that lives in the
    /// <c>unity-ui-document-design-system</c> submodule, junctioned into
    /// <c>Assets/DesignSystem</c>. This file binds runtime data + callbacks
    /// to the named elements and toggles the <c>.mobile</c> class for the
    /// responsive flip — no inline styling.
    ///
    /// Why drive layout via UXML+USS instead of the C#-style API: the
    /// design system encodes a long tail of "Unity UI Toolkit gotchas"
    /// (toggle knob injection, slider track centering, dropdown popup
    /// scope, etc.) as USS rules. Reusing them here keeps the demo
    /// visually consistent with the design system showcase AND surfaces
    /// any rough edge in the system before another consumer hits it.
    /// </summary>
    public class DemoUI : MonoBehaviour
    {
        // Match the @media (max-width: 767px) rule in the WebGL template
        // and the design system's `.mobile` overrides so the C#-driven
        // class flip and the surrounding USS / template fire on the same
        // viewport width.
        const float MOBILE_BREAKPOINT = 768f;
        const string UXML_RESOURCE_PATH = "UI/DemoUI";
        const string MOBILE_CLASS = "mobile";
        const string STATUS_VISIBLE_CLASS = "is-visible";

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern float MeshFractureDemo_GetDevicePixelRatio();
#endif

        DemoBootstrap _bootstrap;
        UIDocument _doc;
        VisualElement _root;
        VisualElement _statusToast;
        Label _statusLabel;
        IVisualElementScheduledItem _statusFade;

        // Captured per-element so the responsive class flip + live wiring
        // doesn't need to re-Query every callback.
        VisualElement _panelTitle;
        VisualElement _panelPromo;
        VisualElement _panelInstructions;
        VisualElement _panelControls;

        // Slider value displays — design-system sliders surface their value
        // as a separate label in the section header (the showcase pattern),
        // not via show-input-field which inherits the editor-theme inset box.
        Label _fragValueLabel;
        Label _forceValueLabel;

        // "Fracture All" button parts. The button doubles as the loading
        // indicator: while pre-bakes are pending, it disables, the refresh
        // glyph appears, and the label flips to "Baking...". DesignSystemRuntime
        // handles the rotation tick.
        Button _fractureBtn;
        VisualElement _fractureIcon;
        Label _fractureLabel;

        // Status toast pieces — the icon switches between the static info
        // glyph and a spinning refresh glyph depending on whether the
        // status reflects an active rebake.
        VisualElement _statusIcon;

        // Last applied baking state so Update() only mutates the DOM on
        // transitions rather than every frame.
        bool _lastBakingState;

        public bool PointerOverUI { get; private set; }

        public void AttachTo(DemoBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
        }

        void Start()
        {
            EnsureInputSystem();
            _doc = gameObject.AddComponent<UIDocument>();
            _doc.panelSettings = MakePanelSettings();
            _doc.visualTreeAsset = Resources.Load<VisualTreeAsset>(UXML_RESOURCE_PATH);
            if (_doc.visualTreeAsset == null)
            {
                Debug.LogError($"[MeshFractureDemo] Could not load {UXML_RESOURCE_PATH}.uxml. " +
                               "Did the design-system junction (Assets/DesignSystem) get created? " +
                               "See README 'Cloning' section.");
            }

            // The design system's runtime helper auto-attaches via
            // SceneManager.sceneLoaded — but our UIDocument is created in
            // Start() AFTER that event has already fired, so we add the
            // component explicitly. EnsureToggleKnobs / spinner tick / etc.
            // then handle the toggle pill rendering on a 250 ms scan.
            gameObject.AddComponent<DesignSystemRuntime>();

            // UIDocument doesn't construct rootVisualElement until OnEnable
            // attaches the panel. AddComponent runs Awake but defers OnEnable
            // by one frame in some versions of Unity 6 — wait until the root
            // is actually available before binding into it.
            StartCoroutine(WaitForRootThenBuild());
        }

        System.Collections.IEnumerator WaitForRootThenBuild()
        {
            int safety = 60;
            while (safety-- > 0 && (_doc == null || _doc.rootVisualElement == null))
                yield return null;

            if (_doc == null || _doc.rootVisualElement == null)
            {
                Debug.LogWarning("[MeshFractureDemo] DemoUI rootVisualElement never appeared — UI not built.");
                yield break;
            }
            BindLayout();
        }

        // =================================================================
        // Bind UXML elements to data + callbacks
        // =================================================================

        void BindLayout()
        {
            _root = _doc.rootVisualElement;
            if (_root == null) return;

            _panelTitle        = _root.Q<VisualElement>("panel-title");
            _panelPromo        = _root.Q<VisualElement>("panel-promo");
            _panelInstructions = _root.Q<VisualElement>("panel-instructions");
            _panelControls     = _root.Q<VisualElement>("panel-controls");
            _statusToast       = _root.Q<VisualElement>("status");
            _statusLabel       = _root.Q<Label>("status-text");
            _statusIcon        = _root.Q<VisualElement>("status-icon");
            _fragValueLabel    = _root.Q<Label>("frag-value");
            _forceValueLabel   = _root.Q<Label>("force-value");
            _fractureBtn       = _root.Q<Button>("fracture-all");
            _fractureIcon      = _root.Q<VisualElement>("fracture-icon");
            _fractureLabel     = _root.Q<Label>("fracture-label");

            BindPromoLinks();
            BindControls();

            _root.RegisterCallback<GeometryChangedEvent>(_ => ApplyResponsive());
            ApplyResponsive();

            // Sync the bake-state visuals up-front so the button is
            // disabled with a spinning glyph during the initial pre-bake
            // pass (typically ~8 frames after Start, one bake per frame).
            // _lastBakingState defaults to false; flip it explicitly so
            // the next Update tick only fires UpdateBakingState on a real
            // transition.
            bool baking = _bootstrap != null && _bootstrap.IsBaking;
            UpdateBakingState(baking);
            _lastBakingState = baking;

            // Track whether the pointer is over an interactive surface.
            // Each panel's wrapper has the default picking mode (Position),
            // so PointerEnter/Leave fire as expected; the .demo-root and
            // .demo-status are picking-mode=Ignore so they don't count.
            foreach (var panel in new[] { _panelTitle, _panelPromo, _panelInstructions, _panelControls })
            {
                if (panel == null) continue;
                panel.RegisterCallback<PointerEnterEvent>(_ => PointerOverUI = true);
                panel.RegisterCallback<PointerLeaveEvent>(_ => PointerOverUI = false);
            }
        }

        void BindPromoLinks()
        {
            // Top-right call-to-action — primary GitHub button only.
            BindButtonLink("promo-github", "https://github.com/sinanata/unity-mesh-fracture");

            // Title-block credits row — text-link Labels for the Leap of
            // Legends Steam wishlist + Kenney asset attribution. Labels
            // (not Buttons) so the typography sits on the same baseline as
            // the title without Unity's button-bg / padding / border
            // creeping in. ClickEvent fires reliably on any VisualElement
            // with default picking-mode=Position.
            BindLabelLink("credit-steam",  "https://store.steampowered.com/app/2269500/");
            BindLabelLink("credit-kenney", "https://kenney.nl/");
        }

        void BindButtonLink(string elementName, string url)
        {
            var btn = _root.Q<Button>(elementName);
            if (btn != null) btn.clicked += () => Application.OpenURL(url);
        }

        void BindLabelLink(string elementName, string url)
        {
            var lbl = _root.Q<Label>(elementName);
            if (lbl != null)
                lbl.RegisterCallback<ClickEvent>(_ => Application.OpenURL(url));
        }

        void BindControls()
        {
            var fragSlider = _root.Q<SliderInt>("frag-slider");
            if (fragSlider != null)
            {
                fragSlider.value = _bootstrap.FragmentCount;
                if (_fragValueLabel != null) _fragValueLabel.text = _bootstrap.FragmentCount.ToString();
                fragSlider.RegisterValueChangedCallback(evt =>
                {
                    _bootstrap.SetFragmentCount(evt.newValue);
                    if (_fragValueLabel != null) _fragValueLabel.text = evt.newValue.ToString();
                    ShowStatus($"Fragment count: {evt.newValue} (re-baking)", spinning: true);
                });
            }

            var forceSlider = _root.Q<Slider>("force-slider");
            if (forceSlider != null)
            {
                forceSlider.value = _bootstrap.ExplosionForce;
                if (_forceValueLabel != null) _forceValueLabel.text = Mathf.RoundToInt(_bootstrap.ExplosionForce).ToString();
                forceSlider.RegisterValueChangedCallback(evt =>
                {
                    _bootstrap.SetExplosionForce(evt.newValue);
                    if (_forceValueLabel != null) _forceValueLabel.text = Mathf.RoundToInt(evt.newValue).ToString();
                });
            }

            var trailsToggle = _root.Q<Toggle>("trails-toggle");
            if (trailsToggle != null)
            {
                trailsToggle.value = _bootstrap.TrailsEnabled;
                trailsToggle.RegisterValueChangedCallback(evt =>
                    _bootstrap.SetTrailsEnabled(evt.newValue));
            }

            if (_fractureBtn != null)
                _fractureBtn.clicked += () => { _bootstrap.FractureAll(); ShowStatus("Fractured all"); };

            var restoreBtn  = _root.Q<Button>("restore-all");
            if (restoreBtn != null)
                restoreBtn.clicked += () => { _bootstrap.RestoreAll(); ShowStatus("Restored"); };
        }

        // =================================================================
        // Per-frame state sync — disable Fracture All while baking
        // =================================================================

        void Update()
        {
            if (_root == null || _bootstrap == null) return;
            bool baking = _bootstrap.IsBaking;
            if (baking == _lastBakingState) return;   // skip redundant DOM mutations
            _lastBakingState = baking;
            UpdateBakingState(baking);
        }

        void UpdateBakingState(bool baking)
        {
            if (_fractureBtn != null) _fractureBtn.SetEnabled(!baking);
            if (_fractureLabel != null)
                _fractureLabel.text = baking ? "Baking..." : "Fracture all (Space)";
            ToggleClass(_fractureIcon, "is-spinning", baking);
            ToggleClass(_fractureIcon, "demo-bake-icon--visible", baking);
        }

        static void ToggleClass(VisualElement el, string cls, bool on)
        {
            if (el == null) return;
            bool has = el.ClassListContains(cls);
            if (on && !has) el.AddToClassList(cls);
            else if (!on && has) el.RemoveFromClassList(cls);
        }

        // =================================================================
        // Responsive — toggle .mobile on root; the rest is USS
        // =================================================================

        void ApplyResponsive()
        {
            if (_root == null) return;
            float w = _root.layout.width;
            if (w <= 0f || float.IsNaN(w))
            {
                float dpr = GetEffectiveDpr();
                w = Screen.width / Mathf.Max(1f, dpr);
            }

            bool mobile = w < MOBILE_BREAKPOINT;
            if (mobile && !_root.ClassListContains(MOBILE_CLASS))
                _root.AddToClassList(MOBILE_CLASS);
            else if (!mobile && _root.ClassListContains(MOBILE_CLASS))
                _root.RemoveFromClassList(MOBILE_CLASS);
        }

        // =================================================================
        // Status toast
        // =================================================================

        public void ShowStatus(string text, bool spinning = false)
        {
            if (_statusToast == null || _statusLabel == null) return;
            _statusLabel.text = text;

            // Swap the toast's icon between the static info glyph and a
            // spinning refresh glyph based on whether the status reflects
            // an in-flight bake. The refresh icon ticks via DesignSystemRuntime
            // as long as it carries `is-spinning`. The toast itself fades
            // after 1.5 s — the persistent baking indicator is the
            // disabled Fracture All button (UpdateBakingState).
            ToggleClass(_statusIcon, "ds-icon--info",     !spinning);
            ToggleClass(_statusIcon, "ds-icon--refresh",   spinning);
            ToggleClass(_statusIcon, "is-spinning",        spinning);

            if (!_statusToast.ClassListContains(STATUS_VISIBLE_CLASS))
                _statusToast.AddToClassList(STATUS_VISIBLE_CLASS);
            _statusFade?.Pause();
            _statusFade = _statusToast.schedule.Execute(() =>
                _statusToast.RemoveFromClassList(STATUS_VISIBLE_CLASS)
            ).StartingIn(1500);
        }

        // =================================================================
        // Panel / EventSystem
        // =================================================================

        static PanelSettings MakePanelSettings()
        {
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.name = "DemoUIPanelSettings";

            // Required for Buttons / Toggles / Sliders to render at all — the
            // default Unity theme provides their base styling, and without
            // it controls render as unstyled black rectangles. The TSS file
            // just imports `unity-theme://default`, the same file Unity auto-
            // creates for fresh PanelSettings.
            var theme = Resources.Load<ThemeStyleSheet>("UnityDefaultRuntimeTheme");
            if (theme != null) ps.themeStyleSheet = theme;
            else Debug.LogWarning("[MeshFractureDemo] UnityDefaultRuntimeTheme.tss missing in Resources — UI controls may render unstyled.");

            ps.scaleMode = PanelScaleMode.ConstantPixelSize;
            ps.scale = GetEffectiveDpr();
            ps.sortingOrder = 1;
            ps.targetDisplay = 0;
            ps.clearColor = false;        // overlay over the 3D camera
            ps.colorClearValue = new Color(0, 0, 0, 0);
            return ps;
        }

        static float GetEffectiveDpr()
        {
            // CSS-pixel-correct rendering on Retina / high-DPR canvases.
            // matchWebGLToCanvasSize=true makes Screen.width report buffer
            // pixels, so without DPR scaling on PanelSettings.scale a 36 px
            // ds-btn renders at 18 CSS-px on a Retina display — which the
            // showcase repo discovered in a separate bug.
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                float dpr = MeshFractureDemo_GetDevicePixelRatio();
                if (dpr > 0f) return dpr;
            }
            catch { /* fall through */ }
#endif
            float dpi = Screen.dpi;
            if (dpi <= 0f) return 1f;
            return Mathf.Max(1f, dpi / 96f);
        }

        static void EnsureInputSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }
    }
}
