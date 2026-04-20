using UnityEngine;

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
    private float low, high;
    private float lowest, highest;

    private GUIStyle style;
    private Rect rect;
    private float memoryMB;

    void Start()
    {
        style = new GUIStyle();
        style.fontSize = fontSize;
        style.normal.textColor = Color.white;

        rect = new Rect(position.x, position.y, 300, 100);

        low = 999f;
        high = 0f;
    }

    void Update()
    {
        frameCount++;
        elapsedTime += Time.deltaTime;

        float currentFPS = 1.0f / Time.deltaTime;
        if (low > currentFPS)
        {
            low = currentFPS;
        }
        if (high < currentFPS)
        {
            high = currentFPS;
        }

        if (elapsedTime >= updateInterval)
        {
            fps = frameCount / elapsedTime;
            lowest = low;
            highest = high;
            if (showMemory)
                memoryMB = System.GC.GetTotalMemory(false) / 1024f / 1024f;

            frameCount = 0;
            elapsedTime = 0f;
            low = 999f;
            high = 0;
        }
    }

    void OnGUI()
    {
        if (style == null)
        {
            style = new GUIStyle();
            style.fontSize = fontSize;
            style.normal.textColor = Color.white;
        }

        string text = $"FPS: {fps:F1} Low: {lowest:F1} High: {highest:F1}";
        if (showMemory)
            text += $"\nMemory: {memoryMB:F1} MB";

        GUI.Label(rect, text, style);
    }
}