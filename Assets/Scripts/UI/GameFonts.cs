using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace ParticleLife.UI
{
    /// <summary>
    /// Fonts that ship in the player. WebGL has no OS fonts, so UI Toolkit and
    /// runtime TMP must use assets under Resources instead of CreateDynamicFontFromOSFont.
    /// </summary>
    public static class GameFonts
    {
        private const string TmpCjkResource = "Fonts & Materials/NotoSansSC-VariableFont_wght SDF";

        private static TMP_FontAsset _tmpCjk;
        private static Font _builtin;

        public static TMP_FontAsset TmpCjk
        {
            get
            {
                if (_tmpCjk == null)
                    _tmpCjk = Resources.Load<TMP_FontAsset>(TmpCjkResource);
                return _tmpCjk;
            }
        }

        public static Font Builtin
        {
            get
            {
                if (_builtin == null)
                {
                    _builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (_builtin == null)
                        _builtin = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
                return _builtin;
            }
        }

        public static void ApplyTo(TextMeshProUGUI tmp)
        {
            if (tmp == null) return;
            if (TmpCjk != null)
                tmp.font = TmpCjk;
        }

        public static void ApplyTo(Text text)
        {
            if (text == null || Builtin == null) return;
            text.font = Builtin;
        }

        public static void ApplyTo(VisualElement root)
        {
            if (root == null) return;

            Font font = TmpCjk != null ? TmpCjk.sourceFontFile : null;
            if (font == null)
                font = Builtin;
            if (font != null)
                root.style.unityFontDefinition = FontDefinition.FromFont(font);
        }

        public static void EnableTmpOnCanvas(Canvas canvas)
        {
            if (canvas == null) return;
            canvas.additionalShaderChannels |=
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;
        }
    }
}
