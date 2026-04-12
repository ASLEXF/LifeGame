using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParticleLife.Input
{
    /// <summary>
    /// Reads player input via Unity Input System actions.
    /// Supports keyboard, gamepad, and mobile touch out of the box.
    ///
    /// ── Inspector setup (one-time after adding this component) ───────────────
    ///
    /// _moveAction   — Add Binding → 2D Vector Composite
    ///                   Up:    Keyboard W  + Keyboard upArrow
    ///                   Down:  Keyboard S  + Keyboard downArrow
    ///                   Left:  Keyboard A  + Keyboard leftArrow
    ///                   Right: Keyboard D  + Keyboard rightArrow
    ///                 Add Binding → &lt;Gamepad&gt;/leftStick
    ///                 Mobile: place an OnScreenStick in your UI canvas and set its
    ///                 Control Path to match this action's binding path.
    ///
    /// _shieldAction — Add Binding → &lt;Keyboard&gt;/f
    ///                 Add Binding → &lt;Gamepad&gt;/buttonSouth
    ///                 Mobile: place an OnScreenButton and point it at this action.
    ///
    /// ── Pointer position ─────────────────────────────────────────────────────
    /// PointerWorldPosition returns the primary touch position on mobile or the
    /// mouse cursor position on desktop. PlayerControl uses this for tap/click-to-
    /// move direction.
    /// </summary>
    public class GameInput : MonoBehaviour
    {
        [Header("输入动作")]
        [Tooltip("移动动作：配置 2D Vector Composite（WASD / 方向键）+ 手柄左摇杆；移动端 OnScreenStick 自动绑定")]
        [SerializeField] private InputAction _moveAction;
        [Tooltip("技能动作：配置 F 键 + 手柄 South 键；移动端 OnScreenButton 自动绑定")]
        [SerializeField] private InputAction _shieldAction;

        [Header("设置")]
        [Tooltip("摇杆死区——低于此值视为无输入（适用于手柄和 OnScreenStick）")]
        [SerializeField] private float _deadzone = 0.15f;

        /// <summary>Normalized movement direction this frame. Zero when no directional input.</summary>
        public float2 DirectionThisFrame    { get; private set; }

        /// <summary>
        /// World-space pointer position this frame.
        /// On mobile: primary touch position. On desktop: mouse cursor position.
        /// Used by PlayerControl for tap / click-to-move direction.
        /// </summary>
        public float2 PointerWorldPosition  { get; private set; }

        /// <summary>True on the frame the shield skill key / button is pressed.</summary>
        public bool   ShieldPressed         { get; private set; }

        private Camera _mainCamera;

        private void Awake()
        {
            _mainCamera = Camera.main;
            EnsureDefaultBindings();
            _moveAction?.Enable();
            _shieldAction?.Enable();
        }

        /// <summary>
        /// Adds sensible default bindings when the Inspector fields have not been
        /// configured. Inspector-configured bindings are left untouched.
        /// </summary>
        private void EnsureDefaultBindings()
        {
            if (_moveAction != null && _moveAction.bindings.Count == 0)
            {
                _moveAction.AddCompositeBinding("2DVector")
                    .With("Up",    "<Keyboard>/w")
                    .With("Up",    "<Keyboard>/upArrow")
                    .With("Down",  "<Keyboard>/s")
                    .With("Down",  "<Keyboard>/downArrow")
                    .With("Left",  "<Keyboard>/a")
                    .With("Left",  "<Keyboard>/leftArrow")
                    .With("Right", "<Keyboard>/d")
                    .With("Right", "<Keyboard>/rightArrow");
                _moveAction.AddBinding("<Gamepad>/leftStick");
            }

            if (_shieldAction != null && _shieldAction.bindings.Count == 0)
            {
                _shieldAction.AddBinding("<Keyboard>/f");
                _shieldAction.AddBinding("<Gamepad>/buttonSouth");
            }
        }

        private void OnDestroy()
        {
            _moveAction?.Disable();
            _shieldAction?.Disable();
        }

        private void Update()
        {
            DirectionThisFrame   = SampleDirection();
            PointerWorldPosition = SamplePointerWorldPos();
            // triggered is true on the frame the action fires (button pressed);
            // compatible with all Input System versions unlike WasPressedThisFrame().
            ShieldPressed        = _shieldAction != null && _shieldAction.triggered;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private float2 SampleDirection()
        {
            if (_moveAction == null) return float2.zero;

            Vector2 raw = _moveAction.ReadValue<Vector2>();
            float2  dir = new float2(raw.x, raw.y);
            float   len = math.length(dir);

            // Apply deadzone and normalise. Values below deadzone are treated as zero
            // to avoid stick drift on gamepads and imprecise on-screen sticks.
            return len > _deadzone ? dir / len : float2.zero;
        }

        private float2 SamplePointerWorldPos()
        {
            if (_mainCamera == null) return float2.zero;

            // Primary touch takes priority so mobile tap-to-move works correctly.
            // Fall back to mouse on desktop.
            Vector2 screenPos;
            var touchscreen = Touchscreen.current;
            if (touchscreen != null && touchscreen.primaryTouch.press.isPressed)
            {
                screenPos = touchscreen.primaryTouch.position.ReadValue();
            }
            else if (Mouse.current != null)
            {
                screenPos = Mouse.current.position.ReadValue();
            }
            else
            {
                return float2.zero;
            }

            Vector3 world = _mainCamera.ScreenToWorldPoint(
                new Vector3(screenPos.x, screenPos.y, -_mainCamera.transform.position.z));
            return new float2(world.x, world.y);
        }
    }
}
