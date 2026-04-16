using System.Collections;
using ParticleLife.Management;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ParticleLife.UI
{
    /// <summary>
    /// Main menu screen controller. Manages the five-stage entry/exit animation
    /// sequence and routes "start game" input to GameStateManager.
    ///
    /// State machine:
    ///   Initializing → FadeIn → Idle → FadeOut → Closed
    ///
    /// The menu reacts to GameState.MainMenu (show) and GameState.Running (close).
    /// It does NOT own game state — it calls GameStateManager.TransitionTo(Running)
    /// via the event pathway after the exit animation completes.
    ///
    /// ─────────────────────────────────────────────────────────────────────────
    /// Unity Editor Setup
    /// ─────────────────────────────────────────────────────────────────────────
    ///
    /// Canvas  (Screen Space Overlay)
    ///   Canvas Scaler: Scale With Screen Size, 1920×1080, Match=0.5
    ///   └── MainMenuRoot  ← attach MainMenuScreen, add CanvasGroup  (_rootGroup)
    ///       ├── OverlayMask      Image, Color #000 alpha 0.45, stretch full,
    ///       │                    Raycast Target = false
    ///       ├── TitleGroup       Empty GameObject + CanvasGroup (_titleGroup)
    ///       │                    Anchored at 50%/60%, pivot 0.5/0.5
    ///       │   ├── Text_Title   TMP 72pt Noto Sans SC Black #F0F0F0  (_titleText)
    ///       │   └── Text_Sub     TMP 13pt Noto Sans SC Regular #8A9BA8 "v0.1.0"
    ///       ├── ButtonGroup      Empty GameObject + CanvasGroup (_buttonGroup)
    ///       │                    Vertical Layout Group, spacing 16, anchored 50%/42%
    ///       │   ├── Btn_Start    Button  (_startButton)  label "▶ 开始游戏"
    ///       │   ├── Btn_Config   Button  (_configButton) label "⚙ 配置引力矩阵"
    ///       │   └── Btn_Lang     Button  (_langButton)   label "EN"  ← 小号样式
    ///       ├── HintBar          Image #000 alpha 0.55, bottom strip, height 40
    ///       │   └── Text_Hint    TMP 12pt #6B7C87                    (_hintText)
    ///       └── BlackFade        Image #000 alpha 0, stretch full    (_blackFade)
    ///
    /// Inspector wiring required:
    ///   _rootGroup    → MainMenuRoot CanvasGroup
    ///   _titleGroup   → TitleGroup CanvasGroup
    ///   _titleRect    → TitleGroup RectTransform
    ///   _titleText    → Text_Title TextMeshProUGUI
    ///   _buttonGroup  → ButtonGroup CanvasGroup
    ///   _blackFade    → BlackFade Image
    ///   _startButton  → Btn_Start Button
    ///   _configButton → Btn_Config Button
    ///   _langButton   → Btn_Lang Button (ButtonGroup 内，初始文字 "EN")
    ///   _hintText       → Text_Hint TMP (optional — hidden if null)
    ///   _gameState      → GameStateManager
    ///   _matrixConfigUI → MatrixConfigUI component (same scene)
    /// ─────────────────────────────────────────────────────────────────────────
    /// </summary>
    public class MainMenuScreen : MonoBehaviour
    {
        // ── Visual constants ──────────────────────────────────────────────────

        /// <summary>标题文字颜色 #F0F0F0</summary>
        private static readonly Color ColorTitle      = new Color(0.941f, 0.941f, 0.941f, 1f);

        /// <summary>遮罩颜色 #000 alpha 0.45</summary>
        private static readonly Color ColorOverlay    = new Color(0f,     0f,     0f,     0.45f);

        /// <summary>主按钮边框默认色 #FFFFFF alpha 0.18</summary>
        private static readonly Color ColorBtnDefault = new Color(1f,     1f,     1f,     0.18f);

        /// <summary>主按钮边框焦点色 #5BB8FF</summary>
        private static readonly Color ColorBtnFocus   = new Color(0.357f, 0.722f, 1f,     1f);

        /// <summary>底部提示文字颜色 #6B7C87</summary>
        private static readonly Color ColorHint       = new Color(0.42f,  0.486f, 0.529f, 1f);

        // ── Animation timing ──────────────────────────────────────────────────

        private const float FadeInDuration        = 1.2f;   // 整体 CanvasGroup 0→1
        private const float TitleDropDelay        = 0.8f;   // 标题下落前等待
        private const float TitleDropDuration     = 0.6f;   // 标题下落动画时长
        private const float TitleDropOffset       = 20f;    // 初始 Y 偏移（像素）
        private const float ButtonFadeInDuration  = 0.5f;   // 按钮组淡入
        private const float ExitButtonFadeOut     = 0.25f;  // 离场：按钮组淡出
        private const float ExitTitleDuration     = 0.4f;   // 离场：标题放大+淡出
        private const float ExitBlackFadeDuration = 0.5f;   // 离场：全黑遮盖

        // ── Inspector references ──────────────────────────────────────────────

        [Header("Canvas 引用")]
        [SerializeField] private CanvasGroup  _rootGroup;
        [SerializeField] private CanvasGroup  _titleGroup;
        [SerializeField] private RectTransform _titleRect;
        [SerializeField] private CanvasGroup  _buttonGroup;
        [SerializeField] private Image        _blackFade;

        [Header("按钮引用")]
        [SerializeField] private Button _startButton;
        [SerializeField] private Button _configButton;
        [SerializeField] private Button _langButton;

        [Header("文本引用")]
        [SerializeField] private TMPro.TextMeshProUGUI _titleText;

        [Header("可选引用")]
        [SerializeField] private TMPro.TextMeshProUGUI _hintText;

        [Header("游戏系统引用")]
        [SerializeField] private GameStateManager  _gameState;
        [SerializeField] private MatrixConfigUI    _matrixConfigUI;

        // ── State machine ─────────────────────────────────────────────────────

        private enum MenuState
        {
            Initializing,
            FadeIn,
            Idle,
            FadeOut,
            Closed,
        }

        private MenuState _menuState = MenuState.Initializing;

        // Cached initial title Y so we can restore it if the menu is reshown.
        private float _titleBaseY;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            // Black cover starts fully opaque — RunFadeIn lifts it.
            // rootGroup stays at 1 so the cover is immediately visible; interaction
            // is still disabled until the entrance animation completes.
            _rootGroup.alpha          = 1f;
            _rootGroup.interactable   = false;
            _rootGroup.blocksRaycasts = false;

            _titleGroup.alpha  = 0f;
            _buttonGroup.alpha = 0f;

            if (_blackFade != null)
            {
                Color c = _blackFade.color;
                _blackFade.color = new Color(c.r, c.g, c.b, 1f);
            }

            _titleBaseY = _titleRect != null ? _titleRect.anchoredPosition.y : 0f;

            if (_hintText != null)
                _hintText.color = ColorHint;

            ApplyLocalization();
        }

        private void Start()
        {
            EnsureUiInputActionsBound();

            _gameState.OnStateChanged += OnStateChanged;

            _startButton.onClick.AddListener(OnStartClicked);

            _configButton.onClick.AddListener(OnConfigClicked);

            if (_langButton != null)
                _langButton.onClick.AddListener(OnLangClicked);

            if (_matrixConfigUI != null)
                _matrixConfigUI.PanelVisibilityChanged += OnMatrixPanelVisibilityChanged;

            Localization.OnLanguageChanged += OnLanguageChangedHandler;

            // If the game already starts at MainMenu (normal launch), begin fade-in.
            if (_gameState.CurrentState == GameState.MainMenu)
                StartCoroutine(RunFadeIn());
        }

        private void EnsureUiInputActionsBound()
        {
            InputSystemUIInputModule uiModule = EventSystem.current != null
                ? EventSystem.current.GetComponent<InputSystemUIInputModule>()
                : null;

            bool beforePoint = uiModule != null && uiModule.point != null && uiModule.point.action != null;
            bool beforeLeftClick = uiModule != null && uiModule.leftClick != null && uiModule.leftClick.action != null;

            if (uiModule != null && (!beforePoint || !beforeLeftClick))
            {
                InputActionAsset asset = uiModule.actionsAsset;
                if (asset != null)
                {
                    InputAction point = asset.FindAction("UI/Point", false) ?? asset.FindAction("Point", false);
                    InputAction click = asset.FindAction("UI/Click", false) ?? asset.FindAction("Click", false);
                    InputAction move = asset.FindAction("UI/Navigate", false) ?? asset.FindAction("Navigate", false);
                    InputAction submit = asset.FindAction("UI/Submit", false) ?? asset.FindAction("Submit", false);
                    InputAction cancel = asset.FindAction("UI/Cancel", false) ?? asset.FindAction("Cancel", false);
                    InputAction right = asset.FindAction("UI/RightClick", false) ?? asset.FindAction("RightClick", false);
                    InputAction middle = asset.FindAction("UI/MiddleClick", false) ?? asset.FindAction("MiddleClick", false);
                    InputAction scroll = asset.FindAction("UI/ScrollWheel", false) ?? asset.FindAction("ScrollWheel", false);
                    InputAction trackedPos = asset.FindAction("UI/TrackedDevicePosition", false) ?? asset.FindAction("TrackedDevicePosition", false);
                    InputAction trackedRot = asset.FindAction("UI/TrackedDeviceOrientation", false) ?? asset.FindAction("TrackedDeviceOrientation", false);

                    if (point != null) uiModule.point = InputActionReference.Create(point);
                    if (click != null) uiModule.leftClick = InputActionReference.Create(click);
                    if (move != null) uiModule.move = InputActionReference.Create(move);
                    if (submit != null) uiModule.submit = InputActionReference.Create(submit);
                    if (cancel != null) uiModule.cancel = InputActionReference.Create(cancel);
                    if (right != null) uiModule.rightClick = InputActionReference.Create(right);
                    if (middle != null) uiModule.middleClick = InputActionReference.Create(middle);
                    if (scroll != null) uiModule.scrollWheel = InputActionReference.Create(scroll);
                    if (trackedPos != null) uiModule.trackedDevicePosition = InputActionReference.Create(trackedPos);
                    if (trackedRot != null) uiModule.trackedDeviceOrientation = InputActionReference.Create(trackedRot);

                    asset.Enable();
                }
                else
                {
                    uiModule.AssignDefaultActions();
                }
            }

        }

        private void OnDestroy()
        {
            if (_gameState != null)
                _gameState.OnStateChanged -= OnStateChanged;

            if (_matrixConfigUI != null)
                _matrixConfigUI.PanelVisibilityChanged -= OnMatrixPanelVisibilityChanged;

            Localization.OnLanguageChanged -= OnLanguageChangedHandler;
        }

        private void Update()
        {
            if (_menuState != MenuState.Idle) return;

            // Keyboard: Enter or Space
            Keyboard kb = Keyboard.current;
            if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame))
            {
                TriggerStartGame();
                return;
            }

            // Gamepad: South button (Cross on PlayStation, A on Xbox)
            Gamepad gp = Gamepad.current;
            if (gp != null && gp.buttonSouth.wasPressedThisFrame)
                TriggerStartGame();
        }

        // ── State changed handler ─────────────────────────────────────────────

        private void OnStateChanged(GameState state)
        {
            // Currently unused — the menu drives the transition, not the other way.
            // If MainMenu is re-entered from another state in the future, add show logic here.
        }

        // ── Button callbacks ──────────────────────────────────────────────────

        private void OnStartClicked()
        {
            if (_menuState != MenuState.Idle) return;
            TriggerStartGame();
        }

        private void OnConfigClicked()
        {
            if (_menuState != MenuState.Idle) return;
            _matrixConfigUI?.Toggle();
        }

        private void OnMatrixPanelVisibilityChanged(bool isVisible)
        {
            // Hide main menu while matrix panel is open, restore when closed.
            if (isVisible)
            {
                _rootGroup.alpha          = 0f;
                _rootGroup.interactable   = false;
                _rootGroup.blocksRaycasts = false;
            }
            else if (_menuState == MenuState.Idle)
            {
                _rootGroup.alpha          = 1f;
                _rootGroup.interactable   = true;
                _rootGroup.blocksRaycasts = true;
            }
        }

        // ── Start game trigger ────────────────────────────────────────────────

        /// <summary>
        /// Initiates the exit animation sequence. Safe to call multiple times — guarded
        /// by the Idle state check. The simulation continues running as-is; no reset is applied.
        /// </summary>
        private void TriggerStartGame()
        {
            if (_menuState != MenuState.Idle) return;
            _menuState = MenuState.FadeOut;

            _matrixConfigUI?.Hide();

            StartCoroutine(RunFadeOut());
        }

        // ── Language ──────────────────────────────────────────────────────────

        private void OnLangClicked()
        {
            if (_menuState != MenuState.Idle) return;
            Localization.SetLanguage(
                Localization.Current == Localization.Language.Chinese
                    ? Localization.Language.English
                    : Localization.Language.Chinese);
        }

        private void OnLanguageChangedHandler(Localization.Language _) => ApplyLocalization();

        private void ApplyLocalization()
        {
            if (_titleText != null)
                _titleText.text = Localization.Get("title");

            TMPro.TextMeshProUGUI startLabel = _startButton != null
                ? _startButton.GetComponentInChildren<TMPro.TextMeshProUGUI>()
                : null;
            if (startLabel != null)
                startLabel.text = Localization.Get("start");

            TMPro.TextMeshProUGUI configLabel = _configButton != null
                ? _configButton.GetComponentInChildren<TMPro.TextMeshProUGUI>()
                : null;
            if (configLabel != null)
                configLabel.text = Localization.Get("config");

            if (_hintText != null)
                _hintText.text = Localization.Get("hint_keyboard");

            TMPro.TextMeshProUGUI langLabel = _langButton != null
                ? _langButton.GetComponentInChildren<TMPro.TextMeshProUGUI>()
                : null;
            if (langLabel != null)
                langLabel.text = Localization.Get("lang_toggle");
        }

        // ── Animation coroutines ──────────────────────────────────────────────

        /// <summary>
        /// Full entrance sequence:
        ///   1. BlackFade image 1→0 over FadeInDuration (EaseOut) — lifts black cover to reveal scene
        ///   2. After TitleDropDelay: title drops +20px→0 over TitleDropDuration (EaseOut)
        ///      and button group fades in over ButtonFadeInDuration (EaseOut) simultaneously
        ///   3. Enables interaction → Idle state
        /// </summary>
        private IEnumerator RunFadeIn()
        {
            _menuState = MenuState.FadeIn;

            // Reset title position in case this method is called after a previous session.
            if (_titleRect != null)
            {
                Vector2 pos = _titleRect.anchoredPosition;
                _titleRect.anchoredPosition = new Vector2(pos.x, _titleBaseY + TitleDropOffset);
            }

            // Phase 1: lift the black cover (EaseOut — fast initial reveal, smooth finish).
            if (_blackFade != null)
            {
                Color startColor = _blackFade.color;
                yield return Tween(FadeInDuration, EaseOut, t =>
                {
                    _blackFade.color = new Color(startColor.r, startColor.g, startColor.b,
                                                  Mathf.Lerp(1f, 0f, t));
                });
                Color c = _blackFade.color;
                _blackFade.color = new Color(c.r, c.g, c.b, 0f);
            }

            // Phase 2a: wait before title drop.
            float waited = 0f;
            while (waited < TitleDropDelay)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            // Phase 2b: title drop + button fade run concurrently.
            // We drive both from a single tween so they stay in sync.
            float startY = _titleBaseY + TitleDropOffset;

            yield return Tween(TitleDropDuration, EaseOut, t =>
            {
                _titleGroup.alpha = t;

                if (_titleRect != null)
                {
                    Vector2 pos = _titleRect.anchoredPosition;
                    _titleRect.anchoredPosition = new Vector2(pos.x, Mathf.Lerp(startY, _titleBaseY, t));
                }

                // Button group uses a shorter duration embedded in the same tween,
                // clamped so it finishes at ButtonFadeInDuration / TitleDropDuration.
                float btnT = Mathf.Clamp01(t / (ButtonFadeInDuration / TitleDropDuration));
                _buttonGroup.alpha = EaseOut(btnT);
            });

            // Ensure exact final values.
            _titleGroup.alpha  = 1f;
            _buttonGroup.alpha = 1f;
            if (_titleRect != null)
            {
                Vector2 pos = _titleRect.anchoredPosition;
                _titleRect.anchoredPosition = new Vector2(pos.x, _titleBaseY);
            }

            // Enable interaction.
            _rootGroup.interactable   = true;
            _rootGroup.blocksRaycasts = true;
            _menuState = MenuState.Idle;
        }

        /// <summary>
        /// Full exit sequence:
        ///   1. Button group fades out over ExitButtonFadeOut
        ///   2. Title scales 1→1.05 and alpha 1→0 over ExitTitleDuration
        ///   3. BlackFade image fades 0→1 over ExitBlackFadeDuration
        ///   4. GameStateManager.TransitionTo(Running) — game takes over
        ///   5. Self deactivated (Closed state)
        /// </summary>
        private IEnumerator RunFadeOut()
        {
            // Disable interaction immediately so double-clicks cannot fire.
            _rootGroup.interactable   = false;
            _rootGroup.blocksRaycasts = false;

            // Step 1: button group fade out.
            float startAlpha = _buttonGroup.alpha;
            yield return Tween(ExitButtonFadeOut, EaseIn, t =>
            {
                _buttonGroup.alpha = Mathf.Lerp(startAlpha, 0f, t);
            });
            _buttonGroup.alpha = 0f;

            // Step 2: title scale up + fade out.
            Vector3 titleScaleStart = _titleRect != null ? _titleRect.localScale : Vector3.one;
            Vector3 titleScaleEnd   = titleScaleStart * 1.05f;
            float   titleAlphaStart = _titleGroup.alpha;

            yield return Tween(ExitTitleDuration, EaseOut, t =>
            {
                _titleGroup.alpha = Mathf.Lerp(titleAlphaStart, 0f, t);
                if (_titleRect != null)
                    _titleRect.localScale = Vector3.Lerp(titleScaleStart, titleScaleEnd, t);
            });
            _titleGroup.alpha = 0f;

            // Step 3: black fade covers everything.
            if (_blackFade != null)
            {
                Color startColor = _blackFade.color;
                yield return Tween(ExitBlackFadeDuration, Linear, t =>
                {
                    _blackFade.color = new Color(startColor.r, startColor.g, startColor.b,
                                                  Mathf.Lerp(0f, 1f, t));
                });

                Color c = _blackFade.color;
                _blackFade.color = new Color(c.r, c.g, c.b, 1f);
            }

            // Step 4: hand off to game systems.
            _gameState.TransitionTo(GameState.Running);

            // Step 5: hide self — game HUD and simulation are now in control.
            _menuState = MenuState.Closed;
            gameObject.SetActive(false);
        }

        // ── Tween utility ─────────────────────────────────────────────────────

        /// <summary>
        /// Drives a callback over <paramref name="duration"/> seconds using
        /// <paramref name="easing"/> to remap the normalised time [0,1].
        /// Uses unscaled time so animations are not affected by Time.timeScale.
        /// </summary>
        private static IEnumerator Tween(float duration, System.Func<float, float> easing,
                                          System.Action<float> onUpdate)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                onUpdate(easing(Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            onUpdate(easing(1f));
        }

        // ── Easing functions ──────────────────────────────────────────────────

        /// <summary>Quadratic ease-in: slow start, fast finish.</summary>
        private static float EaseIn(float t) => t * t;

        /// <summary>Quadratic ease-out: fast start, slow finish.</summary>
        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

        /// <summary>Linear: no easing.</summary>
        private static float Linear(float t) => t;
    }
}
