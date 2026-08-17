using ParticleLife.Input;
using ParticleLife.UI;
using UnityEngine;
using UnityEngine.UI;

public class PerformancePanel : MonoBehaviour
{
    [Header("FPS")]
    public float updateInterval = 0.5f;

    [Header("Display")]
    public bool showMemory = true;
    public int fontSize = 24;
    public Vector2 position = new Vector2(10, 10);

    private int frameCount;
    private float elapsedTime;
    private float fps;
    private float low = 999f;
    private float high;
    private float lowest;
    private float highest;
    private float memoryMB;

    private RectTransform _labelRect;
    private Text _label;
    private string _lastText;

    private void Awake()
    {
        BuildOverlay();
    }

    private void OnEnable()
    {
        InputStyle.Changed += OnInputStyleChanged;
        ApplyLayout();
    }

    private void OnDisable()
    {
        InputStyle.Changed -= OnInputStyleChanged;
    }

    private void Update()
    {
        frameCount++;
        elapsedTime += Time.unscaledDeltaTime;

        float currentFPS = 1.0f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        if (low > currentFPS) low = currentFPS;
        if (high < currentFPS) high = currentFPS;

        if (elapsedTime < updateInterval)
            return;

        fps = frameCount / elapsedTime;
        lowest = low;
        highest = high;
        if (showMemory)
            memoryMB = System.GC.GetTotalMemory(false) / 1024f / 1024f;

        frameCount = 0;
        elapsedTime = 0f;
        low = 999f;
        high = 0f;

        string text = $"FPS: {fps:F1}  Low: {lowest:F1}  High: {highest:F1}";
        if (showMemory)
            text += $"\nMemory: {memoryMB:F1} MB";

        if (_label != null && text != _lastText)
        {
            _lastText = text;
            _label.text = text;
        }
    }

    private void OnInputStyleChanged(PlayerInputStyle _) => ApplyLayout();

    private void BuildOverlay()
    {
        var canvasGo = new GameObject("PerformancePanelCanvas");
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Above HUD (10) / mobile pad (20), below failure (80) and matrix (200).
        canvas.sortingOrder = 25;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        var labelGo = new GameObject("FpsLabel");
        labelGo.transform.SetParent(canvasGo.transform, false);

        _labelRect = labelGo.AddComponent<RectTransform>();
        _labelRect.anchorMin = new Vector2(1f, 1f);
        _labelRect.anchorMax = new Vector2(1f, 1f);
        _labelRect.pivot = new Vector2(1f, 1f);
        _labelRect.sizeDelta = new Vector2(560f, 90f);

        _label = labelGo.AddComponent<Text>();
        GameFonts.ApplyTo(_label);
        _label.fontSize = fontSize;
        _label.alignment = TextAnchor.UpperRight;
        _label.color = Color.white;
        _label.raycastTarget = false;
        _label.horizontalOverflow = HorizontalWrapMode.Overflow;
        _label.verticalOverflow = VerticalWrapMode.Overflow;
        _label.text = "FPS: --";
        _lastText = _label.text;
    }

    private void ApplyLayout()
    {
        if (_labelRect == null)
            return;

        // Keep clear of the on-screen TAB button in the top-right corner.
        float y = InputStyle.IsTouch ? -110f : -16f;
        _labelRect.anchoredPosition = new Vector2(-16f, y);
    }
}
