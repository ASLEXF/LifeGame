using ParticleLife.Player;
using ParticleLife.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace ParticleLife.Rendering
{
    /// <summary>
    /// Maintains a full-world view while subtly biasing toward the player cluster.
    ///
    /// Zoom:
    ///   orthographicSize = WorldHalfY — the entire world is always visible.
    ///
    /// Position:
    ///   target = playerCentroid * _followStrength
    ///   At 0 the camera is fixed at world center; at 1 it fully tracks the player.
    ///   A value around 0.15 gives a subtle parallax feel without losing context.
    ///
    /// Execution order 30 — runs after PlayerControl (10).
    /// Attach to the Main Camera.
    /// </summary>
    [DefaultExecutionOrder(30)]
    [RequireComponent(typeof(Camera))]
    public class CameraFollow : MonoBehaviour
    {
        [Header("无边界模式")]
        [Tooltip("开启后：orthographicSize 固定为 _unboundedOrthoSize，摄像机完全跟随玩家质心（followStrength = 1）")]
        [SerializeField] private bool  _unboundedMode      = false;
        [Tooltip("无边界模式下的正交摄像机尺寸（世界单位）")]
        [SerializeField] private float _unboundedOrthoSize = 35f;
        [Tooltip("无边界模式下的摄像机平滑时间（秒）。值越小越贴近玩家，0 = 完全跟手。建议 0–0.1")]
        [SerializeField] private float _unboundedSmoothTime = 0.05f;
        [Tooltip("无边界模式下的摄像机最大移动速度（世界单位/秒），0 = 不限制。应 ≥ 玩家最大速度以避免画面滞后")]
        [SerializeField] private float _unboundedMaxSpeed   = 0f;

        [Header("跟随设置")]
        [Tooltip("有界模式下的摄像机偏移强度：0 = 固定居中，1 = 完全跟随。建议 0.1–0.2")]
        [SerializeField][Range(0f, 1f)] private float _followStrength = 0.15f;

        [Tooltip("摄像机平滑时间（秒）")]
        [SerializeField] private float _smoothTime = 0.5f;

        [Tooltip("摄像机最大移动速度（世界单位/秒），0 = 不限制")]
        [SerializeField] private float _maxSpeed   = 20f;

        [Header("引用")]
        [SerializeField] private PlayerControl      _playerControl;
        [SerializeField] private ParticleSimulation _simulation;

        private Camera  _camera;
        private Vector3 _velocity;
        private float   _cameraZ;
        private int     _lastScreenWidth;
        private int     _lastScreenHeight;

        private void Awake()
        {
            _camera  = GetComponent<Camera>();
            _cameraZ = transform.position.z;
            ApplyOrthographicSize();
        }

        private void LateUpdate()
        {
            if (Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight)
                ApplyOrthographicSize();

            // Compute target: world center + small offset toward player
            Vector3 targetPos = new Vector3(0f, 0f, _cameraZ);

            if (_playerControl != null && _playerControl.IsAssigned
                && _playerControl.PlayerParticleCount > 0)
            {
                float2 centroid  = _playerControl.ClusterCentroid;
                float  strength  = _unboundedMode ? 1f : _followStrength;
                targetPos.x = centroid.x * strength;
                targetPos.y = centroid.y * strength;
            }

            float smoothTime = _unboundedMode ? _unboundedSmoothTime : _smoothTime;
            float maxSpeed   = _unboundedMode
                ? (_unboundedMaxSpeed > 0f ? _unboundedMaxSpeed : Mathf.Infinity)
                : (_maxSpeed          > 0f ? _maxSpeed          : Mathf.Infinity);
            transform.position = Vector3.SmoothDamp(
                transform.position,
                targetPos,
                ref _velocity,
                smoothTime,
                maxSpeed);
        }

        private void ApplyOrthographicSize()
        {
            _lastScreenWidth  = Screen.width;
            _lastScreenHeight = Screen.height;

            if (_camera == null || _simulation == null) return;

            _camera.orthographicSize = _unboundedMode ? _unboundedOrthoSize : _simulation.WorldHalfY;
        }
    }
}
