using System.Collections;
using ParticleLife.Management;
using ParticleLife.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ParticleLife.UI
{
    /// <summary>
    /// Failure screen overlay. Appears when GameStateManager transitions to Failed.
    ///
    /// Setup (Unity Editor):
    ///   Canvas (Screen Space Overlay)
    ///     Canvas Scaler: Scale With Screen Size, 1920×1080
    ///     └── FailurePanel  ← attach this component, add CanvasGroup
    ///         ├── Text "你被捕获了"     (any TMP label)
    ///         ├── Text_SurvivalTime    ← _survivalTimeText
    ///         ├── Text_PeakCount       ← _peakCountText
    ///         └── Button_Restart       ← _restartButton  (label: "重新开始  [R]")
    ///
    /// Restart: click the button OR press R.
    /// </summary>
    public class FailureScreen : MonoBehaviour
    {
        [Header("UI 引用")]
        [SerializeField] private CanvasGroup      _canvasGroup;
        [SerializeField] private TextMeshProUGUI  _titleText;
        [SerializeField] private TextMeshProUGUI  _survivalTimeText;
        [SerializeField] private TextMeshProUGUI  _peakCountText;
        [SerializeField] private Button           _restartButton;
        [SerializeField] private TextMeshProUGUI  _restartButtonText;

        [Header("设置")]
        [SerializeField] private float _fadeDuration = 0.3f;

        [Header("引用")]
        [SerializeField] private GameStateManager   _gameState;
        [SerializeField] private ParticleSimulation _simulation;

        private bool  _isVisible;
        private float _cachedSurvivalTime;
        private int   _cachedPeakCount;

        private void Awake()
        {
            _canvasGroup.alpha          = 0f;
            _canvasGroup.interactable   = false;
            _canvasGroup.blocksRaycasts = false;
        }

        private void Start()
        {
            _gameState.OnStateChanged += OnStateChanged;
            _restartButton.onClick.AddListener(OnRestartClicked);
            Localization.OnLanguageChanged += OnLanguageChangedHandler;
        }

        private void OnDestroy()
        {
            if (_gameState != null)
                _gameState.OnStateChanged -= OnStateChanged;

            Localization.OnLanguageChanged -= OnLanguageChangedHandler;
        }

        private void Update()
        {
            if (Keyboard.current.rKey.wasPressedThisFrame)
                OnRestartClicked();
        }

        // ── State handling ────────────────────────────────────────────────────

        private void OnStateChanged(GameState state)
        {
            if (state == GameState.Failed)
                Show();
            else if (state == GameState.Running || state == GameState.MainMenu)
                Hide();
        }

        private void Show()
        {
            _cachedSurvivalTime = _gameState.SessionDuration;
            _cachedPeakCount    = _gameState.PeakParticleCount;

            _canvasGroup.interactable   = true;
            _canvasGroup.blocksRaycasts = true;
            _isVisible = true;

            RefreshText();

            StopAllCoroutines();
            StartCoroutine(FadeTo(1f));
        }

        private void Hide()
        {
            _canvasGroup.interactable   = false;
            _canvasGroup.blocksRaycasts = false;
            _isVisible = false;

            StopAllCoroutines();
            StartCoroutine(FadeTo(0f));
        }

        // ── Restart ───────────────────────────────────────────────────────────

        private void OnRestartClicked()
        {
            _simulation.Reinitialize();
            _gameState.RestartSession();
        }

        // ── Localization ──────────────────────────────────────────────────────

        private void OnLanguageChangedHandler(Localization.Language _)
        {
            if (_isVisible) RefreshText();
        }

        private void RefreshText()
        {
            if (_titleText != null)
                _titleText.text = Localization.Get("fail_title");

            _survivalTimeText.text = string.Format(
                Localization.Get("fail_survival"), Localization.FormatTime(_cachedSurvivalTime));
            _peakCountText.text = string.Format(
                Localization.Get("fail_peak"), _cachedPeakCount);

            if (_restartButtonText != null)
                _restartButtonText.text = Localization.Get("fail_restart");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private IEnumerator FadeTo(float target)
        {
            float start   = _canvasGroup.alpha;
            float elapsed = 0f;

            while (elapsed < _fadeDuration)
            {
                elapsed            += Time.unscaledDeltaTime;
                _canvasGroup.alpha  = Mathf.Lerp(start, target, elapsed / _fadeDuration);
                yield return null;
            }

            _canvasGroup.alpha = target;
        }

    }
}
