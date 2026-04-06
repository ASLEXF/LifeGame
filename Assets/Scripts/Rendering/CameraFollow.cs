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
        [Header("跟随设置")]
        [Tooltip("摄像机偏移强度：0 = 固定居中，1 = 完全跟随。建议 0.1–0.2")]
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
                float2 centroid = _playerControl.ClusterCentroid;
                targetPos.x = centroid.x * _followStrength;
                targetPos.y = centroid.y * _followStrength;
            }

            transform.position = Vector3.SmoothDamp(
                transform.position,
                targetPos,
                ref _velocity,
                _smoothTime,
                _maxSpeed > 0f ? _maxSpeed : Mathf.Infinity);
        }

        private void ApplyOrthographicSize()
        {
            _lastScreenWidth  = Screen.width;
            _lastScreenHeight = Screen.height;

            if (_camera == null || _simulation == null) return;

            _camera.orthographicSize = _simulation.WorldHalfY;
        }
    }
}
