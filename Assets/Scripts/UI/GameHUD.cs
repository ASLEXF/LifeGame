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

        [Header("引用")]
        [SerializeField] private GameStateManager  _gameState;
        [SerializeField] private PlayerControl     _playerControl;
        [SerializeField] private CaptureDetection  _captureDetection;
        [SerializeField] private ClusterDetector   _clusterDetector;

        private void Start()
        {
            _gameState.OnStateChanged += OnStateChanged;
            _captureSlider.minValue = 0f;
            _captureSlider.maxValue = 1f;
            _captureSlider.interactable = false;
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
            string clusterSuffix = clusters > 1 ? $" <color=#ff6644>[{clusters} 团簇]</color>" : "";
            _playerParticleCountText.text = $"玩家粒子数：{_playerControl.MainClusterSize}{clusterSuffix}";
            _particleCountText.text = $"粒子总数：{_clusterDetector.ClusterParticleCount}";
            _survivalTimeText.text  = $"存活：{FormatTime(_gameState.SessionDuration)}";

            float captureFraction = _captureDetection.CaptureDuration > 0f
                ? _captureDetection.CaptureTimer / _captureDetection.CaptureDuration
                : 0f;
            _captureSlider.value = Mathf.Clamp01(captureFraction);
        }

        // ── State handling ────────────────────────────────────────────────────

        private void OnStateChanged(GameState state)
        {
            // Hide HUD the same frame failure is detected
            if (state == GameState.Failed)
                gameObject.SetActive(false);
            else if (state == GameState.Running)
                gameObject.SetActive(true);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string FormatTime(float seconds)
        {
            int m = (int)(seconds / 60);
            int s = (int)(seconds % 60);
            return m > 0 ? $"{m}分{s:D2}秒" : $"{s}秒";
        }
    }
}
