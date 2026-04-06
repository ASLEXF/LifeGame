using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParticleLife.Input
{
    /// <summary>
    /// Reads player directional input and outputs a normalized float2 direction vector.
    /// Supports mouse, keyboard (WASD/arrows), and gamepad (left stick).
    /// Abstracts input device; all consumers read DirectionThisFrame only.
    /// </summary>
    public class GameInput : MonoBehaviour
    {
        [Header("设置")]
        [Tooltip("手柄摇杆死区，低于此值视为无输入")]
        [SerializeField] private float _gamepadDeadzone = 0.15f;

        /// <summary>Normalized input direction this frame. Zero when no input.</summary>
        public float2 DirectionThisFrame { get; private set; }

        /// <summary>Raw mouse world position this frame (used by PlayerControl to compute direction).</summary>
        public float2 MouseWorldPosition { get; private set; }

        private Camera _mainCamera;

        private void Awake()
        {
            _mainCamera = Camera.main;
        }

        private void Update()
        {
            DirectionThisFrame = SampleDirection();
            MouseWorldPosition = SampleMouseWorldPos();
        }

        private float2 SampleDirection()
        {
            // Priority: gamepad > keyboard > mouse
            float2 gamepad = SampleGamepad();
            if (math.length(gamepad) > _gamepadDeadzone)
                return math.normalize(gamepad);

            float2 keyboard = SampleKeyboard();
            if (math.lengthsq(keyboard) > 0f)
                return math.normalize(keyboard);

            // Mouse direction is computed by PlayerControl using cluster center
            // Return zero here; PlayerControl reads MouseWorldPosition directly
            return float2.zero;
        }

        private float2 SampleGamepad()
        {
            var gp = Gamepad.current;
            if (gp == null) return float2.zero;
            var v = gp.leftStick.ReadValue();
            return new float2(v.x, v.y);
        }

        private float2 SampleKeyboard()
        {
            float x = 0f, y = 0f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed)    y += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed)  y -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) x += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed)  x -= 1f;
            return new float2(x, y);
        }

        private float2 SampleMouseWorldPos()
        {
            if (_mainCamera == null) return float2.zero;
            Vector3 worldPos = _mainCamera.ScreenToWorldPoint(Mouse.current.position.ReadValue());
            return new float2(worldPos.x, worldPos.y);
        }
    }
}
