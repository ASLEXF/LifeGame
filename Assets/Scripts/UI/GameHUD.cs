using ParticleLife.Gameplay;
using ParticleLife.Management;
using ParticleLife.Player;
using ParticleLife.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ParticleLife.UI
{
    /// <summary>
    /// In-game HUD: particle count, survival time, and capture progress bar.
    /// Hides itself on the same frame the game transitions to Failed.
    ///
    /// Setup (Unity Editor):
    ///   Canvas (Screen Space Overlay)
    ///     Canvas Scaler: Scale With Screen Size, 1920×1080
    ///     └── HUDRoot  ← attach this component
    ///         ├── Text_ParticleCount   ← _playerParticleCountText   (top-left)
    ///         ├── Text_ParticleCount   ← _particleCountText   (top-left)
    ///         ├── Text_SurvivalTime    ← _survivalTimeText    (top-left, below count)
    ///         └── Slider_Capture       ← _captureSlider       (top or bottom bar)
    ///              Min=0, Max=1, Interactable=false
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        [Header("UI 引用")]
        [SerializeField] private TextMeshProUGUI _playerParticleCountText;
        [SerializeField] private TextMeshProUGUI _particleCountText;
        [SerializeField] private TextMeshProUGUI _survivalTimeText;
        [SerializeField] private Slider          _captureSlider;

        [SerializeField] private Slider          _skillSlider;

        [Header("引用")]
        [SerializeField] private GameStateManager  _gameState;
        [SerializeField] private PlayerControl     _playerControl;
        [SerializeField] private CaptureDetection  _captureDetection;
        [SerializeField] private ClusterDetector   _clusterDetector;
        [SerializeField] private PlayerSkill       _playerSkill;

        private void Start()
        {
            _gameState.OnStateChanged += OnStateChanged;

            // Sync visibility to initial state — GameStateManager now starts at MainMenu.
            gameObject.SetActive(_gameState.CurrentState == GameState.Running);

            _captureSlider.minValue    = 0f;
            _captureSlider.maxValue    = 1f;
            _captureSlider.interactable = false;

            if (_skillSlider != null)
            {
                _skillSlider.minValue    = 0f;
                _skillSlider.maxValue    = 1f;
                _skillSlider.interactable = false;
            }
        }

        private void OnDestroy()
        {
            if (_gameState != null)
                _gameState.OnStateChanged -= OnStateChanged;
        }

        private void Update()
        {
            if (!gameObject.activeSelf) return;

            int clusters = _playerControl.ClusterCount;
            string clusterSuffix = clusters > 1
                ? string.Format(Localization.Get("hud_clusters"), clusters)
                : "";
            _playerParticleCountText.text = string.Format(
                Localization.Get("hud_player_count"), _playerControl.MainClusterSize, clusterSuffix);
            _particleCountText.text = string.Format(
                Localization.Get("hud_total_count"), _clusterDetector.ClusterParticleCount);
            _survivalTimeText.text = string.Format(
                Localization.Get("hud_survival"), Localization.FormatTime(_gameState.SessionDuration));

            float captureFraction = _captureDetection.CaptureDuration > 0f
                ? _captureDetection.CaptureTimer / _captureDetection.CaptureDuration
                : 0f;
            _captureSlider.value = Mathf.Clamp01(captureFraction);

            if (_skillSlider != null && _playerSkill != null)
            {
                float sv;
                if (_playerSkill.IsShieldActive)
                    // Active: drains 1 → 0 as time runs out
                    sv = _playerSkill.ShieldDuration > 0f
                        ? _playerSkill.ShieldTimeRemaining / _playerSkill.ShieldDuration
                        : 0f;
                else if (_playerSkill.CooldownTimeRemaining > 0f)
                    // Cooldown: fills 0 → 1 as it recharges
                    sv = _playerSkill.ShieldCooldown > 0f
                        ? 1f - _playerSkill.CooldownTimeRemaining / _playerSkill.ShieldCooldown
                        : 0f;
                else
                    sv = 1f;   // Ready: full bar

                _skillSlider.value = Mathf.Clamp01(sv);
            }
        }

        // ── State handling ────────────────────────────────────────────────────

        private void OnStateChanged(GameState state)
        {
            // Hide HUD in main menu and on failure; show only when running
            if (state == GameState.Failed || state == GameState.MainMenu)
                gameObject.SetActive(false);
            else if (state == GameState.Running)
                gameObject.SetActive(true);
        }

    }
}
