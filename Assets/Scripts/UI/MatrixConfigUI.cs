using ParticleLife.Core;
using ParticleLife.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace ParticleLife.UI
{
    /// <summary>
    /// In-game gravity matrix editor using Unity UI Toolkit.
    /// Tab key toggles the panel without pausing the simulation.
    ///
    /// Setup (Unity Editor):
    ///   GameObject with UIDocument component:
    ///     PanelSettings: Screen Space Overlay (TargetTexture = none)
    ///     Source Asset: MatrixConfigUI.uxml
    ///   Attach MatrixConfigUI component to the same GameObject.
    ///   Assign _simulation reference in Inspector.
    ///
    /// UXML hierarchy expected (built at runtime if SourceAsset is assigned):
    ///   #matrix-panel
    ///     #header-row
    ///       #randomize-button
    ///       #reset-button
    ///     #grid-container   ← N×N MatrixCell rows injected here
    ///
    /// Each MatrixCell (.matrix-cell) contains:
    ///   Label          ← type pair label e.g. "0→1"
    ///   Slider         ← attraction strength  [-40, 40]
    ///   Foldout        ← collapse repulsion / distance controls
    ///     Slider       ← repulsion strength   [0, 40]
    ///     Slider       ← distance threshold   [1, 10]
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MatrixConfigUI : MonoBehaviour
    {
        [Header("引用")]
        [SerializeField] private ParticleSimulation _simulation;

        // Saved initial state for Reset
        private GravityEntry[] _initialEntries;

        // Per-cell slider references [typeA * typeCount + typeB]
        private Slider[] _attractionSliders;
        private Slider[] _repulsionSliders;
        private Slider[] _distanceSliders;

        private VisualElement _panel;
        private UIDocument    _document;
        private bool          _isOpen;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
        }

        private void Start()
        {
            // Snapshot initial matrix for Reset
            int n = _simulation.TypeCount;
            _initialEntries = new GravityEntry[n * n];
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                _initialEntries[a * n + b] = _simulation.GetGravityEntry(a, b);

            BuildUI();
            _panel.style.display = DisplayStyle.None;
        }

        private void Update()
        {
            if (Keyboard.current.tabKey.wasPressedThisFrame)
                Toggle();
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUI()
        {
            var root = _document.rootVisualElement;
            root.Clear();

            int n = _simulation.TypeCount;
            _attractionSliders = new Slider[n * n];
            _repulsionSliders  = new Slider[n * n];
            _distanceSliders   = new Slider[n * n];

            // ── Panel container ────────────────────────────────────────────
            _panel = new VisualElement();
            _panel.name = "matrix-panel";
            _panel.AddToClassList("matrix-panel");

            // ── Header ─────────────────────────────────────────────────────
            var header = new VisualElement();
            header.AddToClassList("header-row");

            var title = new Label("引力矩阵配置");
            title.AddToClassList("panel-title");
            header.Add(title);

            var randomizeBtn = new Button(OnRandomize) { text = "随机化" };
            randomizeBtn.AddToClassList("action-button");
            header.Add(randomizeBtn);

            var resetBtn = new Button(OnReset) { text = "重置" };
            resetBtn.AddToClassList("action-button");
            header.Add(resetBtn);

            _panel.Add(header);

            // ── Grid hint label ────────────────────────────────────────────
            var hint = new Label("行 = 施力方，列 = 受力方");
            hint.AddToClassList("grid-hint");
            _panel.Add(hint);

            // ── Grid (one row per typeA) ───────────────────────────────────
            var grid = new VisualElement();
            grid.name = "grid-container";
            grid.AddToClassList("grid-container");

            for (int a = 0; a < n; a++)
            {
                var row = new VisualElement();
                row.AddToClassList("matrix-row");

                var rowLabel = new Label($"类型 {a}");
                rowLabel.AddToClassList("row-label");
                row.Add(rowLabel);

                for (int b = 0; b < n; b++)
                {
                    int ia = a, ib = b; // capture for closures
                    int idx = a * n + b;
                    GravityEntry entry = _simulation.GetGravityEntry(a, b);

                    var cell = new VisualElement();
                    cell.AddToClassList("matrix-cell");
                    UpdateCellColor(cell, entry.AttractionStrength);

                    // Pair label
                    var pairLabel = new Label($"{a}→{b}");
                    pairLabel.AddToClassList("cell-label");
                    cell.Add(pairLabel);

                    // Attraction slider
                    var attrSlider = new Slider("引力", -40f, 40f)
                    {
                        value = entry.AttractionStrength,
                        showInputField = true,
                    };
                    attrSlider.AddToClassList("attr-slider");
                    attrSlider.RegisterValueChangedCallback(evt =>
                    {
                        var e = _simulation.GetGravityEntry(ia, ib);
                        e.AttractionStrength = evt.newValue;
                        _simulation.SetGravityEntry(ia, ib, e);
                        UpdateCellColor(cell, evt.newValue);
                    });
                    cell.Add(attrSlider);
                    _attractionSliders[idx] = attrSlider;

                    // Foldout for repulsion + distance
                    var foldout = new Foldout { text = "高级", value = false };
                    foldout.AddToClassList("cell-foldout");

                    var repSlider = new Slider("斥力", 0f, 40f)
                    {
                        value = entry.RepulsionStrength,
                        showInputField = true,
                    };
                    repSlider.RegisterValueChangedCallback(evt =>
                    {
                        var e = _simulation.GetGravityEntry(ia, ib);
                        e.RepulsionStrength = evt.newValue;
                        _simulation.SetGravityEntry(ia, ib, e);
                    });
                    foldout.Add(repSlider);
                    _repulsionSliders[idx] = repSlider;

                    var distSlider = new Slider("距离阈值", 1f, 10f)
                    {
                        value = entry.DistanceThreshold,
                        showInputField = true,
                    };
                    distSlider.RegisterValueChangedCallback(evt =>
                    {
                        var e = _simulation.GetGravityEntry(ia, ib);
                        e.DistanceThreshold = evt.newValue;
                        _simulation.SetGravityEntry(ia, ib, e);
                    });
                    foldout.Add(distSlider);
                    _distanceSliders[idx] = distSlider;

                    cell.Add(foldout);
                    row.Add(cell);
                }

                grid.Add(row);
            }

            _panel.Add(grid);
            root.Add(_panel);

            // Load USS
            var uss = Resources.Load<StyleSheet>("MatrixConfigUI");
            if (uss != null)
                root.styleSheets.Add(uss);
        }

        // ── Toggle ────────────────────────────────────────────────────────────

        private void Toggle()
        {
            _isOpen = !_isOpen;
            _panel.style.display = _isOpen ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ── Buttons ───────────────────────────────────────────────────────────

        private void OnRandomize()
        {
            int n   = _simulation.TypeCount;
            var rng = new System.Random();

            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                int idx = a * n + b;
                var entry = new GravityEntry
                {
                    AttractionStrength = (float)(rng.NextDouble() * 80.0 - 40.0),
                    RepulsionStrength  = (float)(rng.NextDouble() * 20.0 + 2.0),
                    DistanceThreshold  = (float)(rng.NextDouble() * 7.0  + 1.0),
                };
                _simulation.SetGravityEntry(a, b, entry);
                RefreshCell(idx, entry);
            }
        }

        private void OnReset()
        {
            int n = _simulation.TypeCount;
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                int idx   = a * n + b;
                var entry = _initialEntries[idx];
                _simulation.SetGravityEntry(a, b, entry);
                RefreshCell(idx, entry);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void RefreshCell(int idx, GravityEntry entry)
        {
            if (_attractionSliders[idx] != null)
                _attractionSliders[idx].SetValueWithoutNotify(entry.AttractionStrength);
            if (_repulsionSliders[idx] != null)
                _repulsionSliders[idx].SetValueWithoutNotify(entry.RepulsionStrength);
            if (_distanceSliders[idx] != null)
                _distanceSliders[idx].SetValueWithoutNotify(entry.DistanceThreshold);

            // Also update cell background
            var cell = _attractionSliders[idx]?.parent;
            if (cell != null)
                UpdateCellColor(cell, entry.AttractionStrength);
        }

        /// <summary>Interpolates cell background: blue (strong repulsion) → neutral → orange (strong attraction).</summary>
        private static void UpdateCellColor(VisualElement cell, float attractionStrength)
        {
            float t = Mathf.InverseLerp(-40f, 40f, attractionStrength); // 0 = repulsion, 1 = attraction
            Color cold = new Color(0.18f, 0.35f, 0.65f, 0.75f);
            Color warm = new Color(0.75f, 0.35f, 0.10f, 0.75f);
            Color mid  = new Color(0.20f, 0.20f, 0.20f, 0.75f);

            Color bg = t < 0.5f
                ? Color.Lerp(cold, mid, t * 2f)
                : Color.Lerp(mid, warm, (t - 0.5f) * 2f);

            cell.style.backgroundColor = bg;
        }
    }
}
