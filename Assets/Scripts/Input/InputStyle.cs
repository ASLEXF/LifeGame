using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParticleLife.Input
{
    public enum PlayerInputStyle
    {
        Desktop,
        Touch
    }

    /// <summary>
    /// Resolves whether the player should get desktop (keyboard / mouse) or
    /// on-screen touch controls.
    ///
    /// Native Android / iOS stays on Touch. Native desktop stays on Desktop.
    /// WebGL starts from the browser's real input capability (coarse pointer,
    /// hover, maxTouchPoints, UA — including iPadOS desktop-UA) and then
    /// follows the last keyboard vs finger press so a phone in "desktop site"
    /// mode or a tablet with a keyboard still gets the right UI.
    /// </summary>
    public static class InputStyle
    {
        public static PlayerInputStyle Current { get; private set; } = PlayerInputStyle.Desktop;

        public static bool IsTouch => Current == PlayerInputStyle.Touch;

        public static event Action<PlayerInputStyle> Changed;

        private static bool _initialized;
        private static bool _allowRuntimeSwitch;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int WebInput_PrefersTouch();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            Initialize();
        }

        /// <summary>
        /// <paramref name="forceTouchInEditor"/> is honored only in the Editor so
        /// MobileControls can preview the virtual pad.
        /// </summary>
        public static void Initialize(bool forceTouchInEditor = false)
        {
#if UNITY_EDITOR
            Set(forceTouchInEditor ? PlayerInputStyle.Touch : PlayerInputStyle.Desktop, force: true);
            _allowRuntimeSwitch = false;
            _initialized = true;
            return;
#else
            if (_initialized)
                return;

            _initialized = true;

            if (Application.platform == RuntimePlatform.Android
                || Application.platform == RuntimePlatform.IPhonePlayer)
            {
                Set(PlayerInputStyle.Touch, force: true);
                _allowRuntimeSwitch = false;
                return;
            }

            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                Set(DetectWebInitialStyle(), force: true);
                _allowRuntimeSwitch = true;
                return;
            }

            Set(Application.isMobilePlatform ? PlayerInputStyle.Touch : PlayerInputStyle.Desktop, force: true);
            _allowRuntimeSwitch = false;
#endif
        }

        public static void Tick()
        {
            if (!_initialized || !_allowRuntimeSwitch)
                return;

            // Keyboard is unambiguous desktop input. Do not use Mouse: mobile
            // browsers synthesize mouse events after every tap and would flicker.
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame)
            {
                Set(PlayerInputStyle.Desktop);
                return;
            }

            Touchscreen ts = Touchscreen.current;
            if (ts != null && ts.primaryTouch.press.wasPressedThisFrame)
                Set(PlayerInputStyle.Touch);
        }

        private static PlayerInputStyle DetectWebInitialStyle()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                if (WebInput_PrefersTouch() != 0)
                    return PlayerInputStyle.Touch;
            }
            catch (EntryPointNotFoundException)
            {
            }
#endif
            return Application.isMobilePlatform
                ? PlayerInputStyle.Touch
                : PlayerInputStyle.Desktop;
        }

        private static void Set(PlayerInputStyle style, bool force = false)
        {
            if (!force && Current == style)
                return;

            Current = style;
            Changed?.Invoke(style);
        }
    }
}
