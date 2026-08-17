using ParticleLife.Management;
using ParticleLife.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.OnScreen;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ParticleLife.Input
{
    /// <summary>
    /// Builds on-screen virtual controls at runtime when the player is using touch.
    ///
    /// Layout:
    ///   Bottom-left  — Virtual joystick → &lt;Gamepad&gt;/leftStick  → GameInput._moveAction
    ///   Bottom-right — Skill button     → &lt;Gamepad&gt;/buttonSouth → GameInput._shieldAction
    ///   Top-right    — TAB button       → MatrixConfigUI.Toggle()
    ///
    /// Visibility is driven by <see cref="InputStyle"/> (WebGL detects desktop vs
    /// mobile in the browser, then follows keyboard vs finger) and by GameState
    /// so the pad is hidden on the main menu / failure screen.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class MobileControls : MonoBehaviour
    {
        [Header("引用")]
        [SerializeField] private MatrixConfigUI _matrixConfigUI;
        [SerializeField] private GameStateManager _gameState;

        [Header("编辑器测试")]
        [Tooltip("在 Editor 中强制显示虚拟控件（用于布局预览）")]
        [SerializeField] private bool _showInEditor;

        [Header("摇杆")]
        [SerializeField] private float _joystickOuterRadius = 130f;
        [SerializeField] private float _joystickThumbRadius = 58f;
        [SerializeField] private float _joystickMargin      = 50f;

        [Header("技能按钮")]
        [SerializeField] private float _skillButtonSize   = 115f;
        [SerializeField] private float _skillButtonMargin = 50f;

        [Header("TAB 按钮")]
        [SerializeField] private float _tabButtonSize   = 75f;
        [SerializeField] private float _tabButtonMargin = 30f;

        [Header("颜色")]
        [SerializeField] private Color _ringColor  = new Color(1f, 1f, 1f, 0.18f);
        [SerializeField] private Color _thumbColor = new Color(1f, 1f, 1f, 0.40f);
        [SerializeField] private Color _skillColor = new Color(0.28f, 0.65f, 1.00f, 0.55f);
        [SerializeField] private Color _tabColor   = new Color(1f, 1f, 1f, 0.28f);

        private GameObject _canvasGo;
        private TextMeshProUGUI _skillLabel;
        private TextMeshProUGUI _tabLabel;

        private void Awake()
        {
            InputStyle.Initialize(_showInEditor);

            if (_matrixConfigUI == null)
                _matrixConfigUI = FindObjectOfType<MatrixConfigUI>();
            if (_gameState == null)
                _gameState = FindObjectOfType<GameStateManager>();
        }

        private void OnEnable()
        {
            InputStyle.Changed += OnInputStyleChanged;
            Localization.OnLanguageChanged += OnLanguageChanged;
            if (_gameState != null)
                _gameState.OnStateChanged += OnGameStateChanged;
        }

        private void OnDisable()
        {
            InputStyle.Changed -= OnInputStyleChanged;
            Localization.OnLanguageChanged -= OnLanguageChanged;
            if (_gameState != null)
                _gameState.OnStateChanged -= OnGameStateChanged;
        }

        private void Start()
        {
            RefreshVisibility();
        }

        private void Update()
        {
            InputStyle.Tick();
        }

        private void OnInputStyleChanged(PlayerInputStyle _) => RefreshVisibility();

        private void OnGameStateChanged(GameState _) => RefreshVisibility();

        private void OnLanguageChanged(Localization.Language _) => ApplyLabels();

        private void RefreshVisibility()
        {
            bool show = InputStyle.IsTouch
                        && (_gameState == null || _gameState.CurrentState == GameState.Running);

            if (show && _canvasGo == null)
                BuildControls();

            if (_canvasGo != null)
                _canvasGo.SetActive(show);
        }

        private void BuildControls()
        {
            EnsureEventSystem();
            var canvas = CreateCanvas();
            _canvasGo = canvas.gameObject;
            var ct = canvas.transform;

            Sprite circle = CreateCircleSprite(128);
            BuildJoystick(ct, circle);
            BuildSkillButton(ct, circle);
            BuildTabButton(ct);
            ApplyLabels();
        }

        private Canvas CreateCanvas()
        {
            var go = new GameObject("MobileControlsCanvas");
            go.transform.SetParent(transform, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above HUD (10), below failure (80) / main menu (100) / matrix (200).
            canvas.sortingOrder = 20;
            GameFonts.EnableTmpOnCanvas(canvas);

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        private void BuildJoystick(Transform parent, Sprite circle)
        {
            float cx = _joystickMargin + _joystickOuterRadius;
            float cy = _joystickMargin + _joystickOuterRadius;
            float outer = _joystickOuterRadius * 2f;

            var root = new GameObject("Joystick");
            root.transform.SetParent(parent, false);
            var rootRect = root.AddComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.zero;
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.anchoredPosition = new Vector2(cx, cy);
            rootRect.sizeDelta = Vector2.one * outer;

            var center = new Vector2(0.5f, 0.5f);
            MakeImage("Joystick_Ring", root.transform, circle, _ringColor,
                center, center, center,
                Vector2.zero, Vector2.one * outer,
                raycast: false);

            // Full-ring hit target so the stick is easy to grab; thumb is visual only.
            MakeImage("Joystick_Stick", root.transform, circle, Color.clear,
                center, center, center,
                Vector2.zero, Vector2.one * outer,
                raycast: true,
                configure: go =>
                {
                    var stick = go.AddComponent<OnScreenStick>();
                    stick.controlPath = "<Gamepad>/leftStick";
                    stick.movementRange = _joystickOuterRadius - _joystickThumbRadius;

                    MakeImage("Joystick_Thumb", go.transform, circle, _thumbColor,
                        center, center, center,
                        Vector2.zero, Vector2.one * (_joystickThumbRadius * 2f),
                        raycast: false);
                });
        }

        private void BuildSkillButton(Transform parent, Sprite circle)
        {
            MakeImage("Skill_Button", parent, circle, _skillColor,
                new Vector2(1, 0), new Vector2(1, 0), new Vector2(1f, 0f),
                new Vector2(-_skillButtonMargin, _skillButtonMargin),
                Vector2.one * _skillButtonSize,
                raycast: true,
                configure: go =>
                {
                    go.AddComponent<OnScreenButton>().controlPath = "<Gamepad>/buttonSouth";
                    _skillLabel = AddLabel(go, Localization.Get("mobile_skill"), 26);
                });
        }

        private void BuildTabButton(Transform parent)
        {
            var go = new GameObject("Tab_Button");
            go.transform.SetParent(parent, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-_tabButtonMargin, -_tabButtonMargin);
            rect.sizeDelta = Vector2.one * _tabButtonSize;

            var img = go.AddComponent<Image>();
            img.color = _tabColor;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var matrixRef = _matrixConfigUI;
            btn.onClick.AddListener(() => matrixRef?.Toggle());

            _tabLabel = AddLabel(go, Localization.Get("mobile_tab"), 22);
        }

        private void ApplyLabels()
        {
            if (_skillLabel != null)
                _skillLabel.text = Localization.Get("mobile_skill");
            if (_tabLabel != null)
                _tabLabel.text = Localization.Get("mobile_tab");
        }

        private static GameObject MakeImage(
            string name, Transform parent, Sprite sprite, Color color,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 anchoredPos, Vector2 size,
            bool raycast = true,
            System.Action<GameObject> configure = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = raycast;

            configure?.Invoke(go);
            return go;
        }

        private static TextMeshProUGUI AddLabel(GameObject parent, string text, float fontSize)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent.transform, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            GameFonts.ApplyTo(tmp);
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
                return;

            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
        }

        private static Sprite CreateCircleSprite(int resolution)
        {
            var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            float half = resolution * 0.5f;
            float r = half - 1f;

            for (int y = 0; y < resolution; y++)
            for (int x = 0; x < resolution; x++)
            {
                float dx = x - half + 0.5f;
                float dy = y - half + 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(r - dist);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
            tex.Apply();

            return Sprite.Create(tex,
                new Rect(0, 0, resolution, resolution),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit: resolution);
        }
    }
}
