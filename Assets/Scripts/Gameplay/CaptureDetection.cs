using ParticleLife.Management;
using ParticleLife.Simulation;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ParticleLife.Gameplay
{
    /// <summary>
    /// Detects when the player cluster is being overwhelmed by external forces.
    ///
    /// Each frame (LateUpdate, order 20, after PlayerControl at order 10):
    ///   1. Sum ExternalForceOnPlayer across all player-owned particles → externalForce.
    ///   2. Compare against the current frame's player input force → controlRatio.
    ///   3. If externalForce is negligible: decay captureTimer.
    ///      If controlRatio < _captureThreshold: accumulate captureTimer.
    ///      Otherwise: decay captureTimer.
    ///   4. captureTimer ≥ _captureDuration → TransitionTo(Failed).
    ///
    /// CaptureTimer and CaptureDuration are public so FailureScreen and HUD can read them.
    /// </summary>
    [DefaultExecutionOrder(20)]
    public class CaptureDetection : MonoBehaviour
    {
        [Header("捕获判定")]
        [Tooltip("捕获计时器上限（秒）；超过此值触发失败")]
        [SerializeField] private float _captureDuration = 5f;

        [Tooltip("controlRatio 低于此值时判定为被捕获状态（inputForce / externalForce）")]
        [SerializeField][Range(0f, 2f)] private float _captureThreshold = 0.5f;

        [Tooltip("外部合力低于此值时视为无显著威胁，计时器不累加")]
        [SerializeField] private float _minExternalForce = 1f;

        [Tooltip("非捕获状态下计时器衰减速率（秒/秒）")]
        [SerializeField] private float _timerDecayRate = 1.5f;

        [Header("引用")]
        [SerializeField] private ParticleSimulation _simulation;
        [SerializeField] private GameStateManager _gameState;

        private float _captureTimer;

        /// <summary>Current capture timer value in seconds. Resets to 0 when not under threat.</summary>
        public float CaptureTimer => _captureTimer;

        /// <summary>Capture duration threshold. Use with CaptureTimer to display a progress bar.</summary>
        public float CaptureDuration => _captureDuration;

        private void OnEnable()
        {
            _gameState.OnStateChanged += OnGameStateChanged;
        }

        private void OnDisable()
        {
            _gameState.OnStateChanged -= OnGameStateChanged;
        }

        private void LateUpdate()
        {
            if (_gameState.CurrentState != GameState.Running) return;

            int count = _simulation.ParticleCount;
            if (count == 0) return;

            NativeArray<bool> isPlayerOwned = _simulation.IsPlayerOwned;
            NativeArray<float2> extForces = _simulation.ExternalForceOnPlayer;

            // Sum external forces over all player-owned particles
            float2 externalForceSum = float2.zero;
            int playerCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                externalForceSum += extForces[i];
                playerCount++;
            }

            if (playerCount == 0) return;

            float extMag = math.length(externalForceSum);
            float inputMag = math.length(_simulation.PlayerInputForce);
            float dt = Time.deltaTime;

            if (extMag < _minExternalForce)
            {
                _captureTimer = math.max(0f, _captureTimer - dt * _timerDecayRate);
                return;
            }

            float controlRatio = inputMag / (extMag + 0.001f);

            if (controlRatio < _captureThreshold)
                _captureTimer += dt;
            else
                _captureTimer = math.max(0f, _captureTimer - dt * _timerDecayRate);

            if (_captureTimer >= _captureDuration)
                _gameState.TransitionTo(GameState.Failed);
        }

    private void OnGameStateChanged(GameState state)
        {
            if (state == GameState.Running)
                _captureTimer = 0f;
        }
    }
}
