using ParticleLife.Core;
using ParticleLife.Management;
using ParticleLife.Persistence;
using ParticleLife.Simulation;
using System;
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
        [SerializeField] private ConfigPersistence  _configPersistence;

        // Per-cell slider references [typeA * typeCount + typeB]
        private Slider[] _attractionSliders;
        private Slider[] _repulsionSliders;
        private Slider[] _distanceSliders;

        private VisualElement _panel;
        private UIDocument    _document;
        private bool          _isOpen;
        private Font          _runtimeUiFont;
        private StyleSheet    _matrixStyleSheet;
        private int           _matrixSizeSnapshot;
        private Label         _saveHintLabel;
        private bool          _wasSaved;
        private Button        _presetSaveButton;
        private VisualElement _presetListContainer;
        private TextField     _presetNameField;
        public event Action<bool> PanelVisibilityChanged;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
        }

        private void Start()
        {
            BuildUI();
            _panel.style.display = DisplayStyle.None;

            Localization.OnLanguageChanged += OnLanguageChangedHandler;
        }

        private void OnDestroy()
        {
            Localization.OnLanguageChanged -= OnLanguageChangedHandler;
        }

        private void OnLanguageChangedHandler(Localization.Language _)
        {
            BuildUI();
            _panel.style.display = _isOpen ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            if (_matrixSizeSnapshot != _simulation.TotalTypeCount)
            {
                bool reopen = _isOpen;
                BuildUI();
                _isOpen = reopen;
                _panel.style.display = reopen ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (kb.tabKey.wasPressedThisFrame)
                Toggle();
            if (_isOpen && kb.escapeKey.wasPressedThisFrame)
                Hide();
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUI()
        {
            var root = _document.rootVisualElement;
            root.Clear();
            root.AddToClassList("matrix-root");
            EnsureRuntimeUiFont();

            if (_runtimeUiFont != null)
                root.style.unityFontDefinition = FontDefinition.FromFont(_runtimeUiFont);

            if (_matrixStyleSheet == null)
                _matrixStyleSheet = Resources.Load<StyleSheet>("MatrixConfigUI");

            if (_matrixStyleSheet != null && !HasStyleSheet(root, _matrixStyleSheet))
                root.styleSheets.Add(_matrixStyleSheet);

            int n = GetEditableTypeCount();
            _matrixSizeSnapshot = _simulation.TotalTypeCount;
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

            var title = new Label(Localization.Get("matrix_title"));
            title.AddToClassList("panel-title");
            header.Add(title);

            // Spacer pushes hint + buttons to the right
            var spacer = new VisualElement();
            spacer.AddToClassList("header-spacer");
            header.Add(spacer);

            _saveHintLabel = new Label();
            _saveHintLabel.AddToClassList("config-loaded-hint");
            if (_wasSaved)
            {
                _saveHintLabel.text = Localization.Get("preset_saved");
                _saveHintLabel.style.display = DisplayStyle.Flex;
            }
            else if (_configPersistence != null && _configPersistence.WasLoadedFromDisk)
            {
                _saveHintLabel.text = Localization.Get("matrix_config_loaded");
                _saveHintLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                _saveHintLabel.style.display = DisplayStyle.None;
            }
            header.Add(_saveHintLabel);

            var presetBtnPresets = PresetPersistence.GetPresets();
            _presetSaveButton = new Button(OnPresetSave) { text = Localization.Get("preset_save") };
            _presetSaveButton.AddToClassList("action-button");
            _presetSaveButton.SetEnabled(presetBtnPresets.Count < PresetPersistence.MaxPresets);
            header.Add(_presetSaveButton);

            var randomizeBtn = new Button(OnRandomize)
            {
                text    = Localization.Get("matrix_randomize"),
                tooltip = Localization.Get("matrix_tip_randomize"),
            };
            randomizeBtn.AddToClassList("action-button");
            header.Add(randomizeBtn);

            var defaultBtn = new Button(OnResetToDefaults)
            {
                text    = Localization.Get("matrix_reset_def"),
                tooltip = Localization.Get("matrix_tip_reset_def"),
            };
            defaultBtn.AddToClassList("action-button");
            header.Add(defaultBtn);

            var closeBtn = new Button(Hide) { text = "✕" };
            closeBtn.AddToClassList("close-button");
            header.Add(closeBtn);

            _panel.Add(header);

            // ── Keyboard hint ──────────────────────────────────────────────
            var kbHint = new Label(Localization.Get("matrix_keyboard_hint"));
            kbHint.AddToClassList("keyboard-hint");
            _panel.Add(kbHint);

            // ── Preset section ─────────────────────────────────────────────
            _panel.Add(BuildPresetSection());

            // ── Grid hint label ────────────────────────────────────────────
            var hint = new Label(Localization.Get("matrix_hint"));
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

                var rowLabel = new Label(string.Format(Localization.Get("matrix_type"), a));
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

                    // Cell header: pair label (left) + advanced toggle (top-right)
                    var cellHeader = new VisualElement();
                    cellHeader.AddToClassList("cell-header");

                    var pairLabel = new Label($"{a}→{b}");
                    pairLabel.AddToClassList("cell-label");
                    cellHeader.Add(pairLabel);

                    var attrHeaderLabel = new Label(Localization.Get("matrix_attraction"));
                    attrHeaderLabel.AddToClassList("cell-attr-header-label");
                    cellHeader.Add(attrHeaderLabel);

                    var advancedContent = new VisualElement();
                    advancedContent.AddToClassList("cell-advanced-content");
                    advancedContent.style.display = DisplayStyle.None;

                    var advancedToggle = new Button(() =>
                    {
                        bool showing = advancedContent.style.display == DisplayStyle.Flex;
                        advancedContent.style.display = showing ? DisplayStyle.None : DisplayStyle.Flex;
                    })
                    { text = Localization.Get("matrix_advanced") };
                    advancedToggle.AddToClassList("cell-advanced-toggle");
                    cellHeader.Add(advancedToggle);
                    cell.Add(cellHeader);

                    // Attraction slider
                    var attrSlider = new Slider("", -40f, 40f)
                    {
                        value = Mathf.Clamp(entry.AttractionStrength, -40f, 40f),
                        showInputField = true,
                    };
                    attrSlider.AddToClassList("attr-slider");
                    attrSlider.RegisterValueChangedCallback(evt =>
                    {
                        if (!IsMatrixIndexValid(ia, ib)) return;
                        var e = _simulation.GetGravityEntry(ia, ib);
                        e.AttractionStrength = Mathf.Clamp(evt.newValue, -40f, 40f);
                        _simulation.SetGravityEntry(ia, ib, e);
                        UpdateCellColor(cell, e.AttractionStrength);
                    });
                    cell.Add(attrSlider);
                    _attractionSliders[idx] = attrSlider;

                    // Advanced content: repulsion + distance sliders (horizontal)
                    var repSlider = new Slider(Localization.Get("matrix_repulsion"), 0f, 40f)
                    {
                        value = Mathf.Clamp(entry.RepulsionStrength, 0f, 40f),
                        showInputField = true,
                    };
                    repSlider.style.width = StyleKeyword.Auto;
                    repSlider.style.flexGrow = 1;
                    repSlider.style.marginRight = 4;
                    repSlider.RegisterValueChangedCallback(evt =>
                    {
                        if (!IsMatrixIndexValid(ia, ib)) return;
                        var e = _simulation.GetGravityEntry(ia, ib);
                        e.RepulsionStrength = Mathf.Clamp(evt.newValue, 0f, 40f);
                        _simulation.SetGravityEntry(ia, ib, e);
                    });
                    advancedContent.Add(repSlider);
                    _repulsionSliders[idx] = repSlider;

                    var distSlider = new Slider(Localization.Get("matrix_distance"), 1f, 10f)
                    {
                        value = Mathf.Clamp(entry.DistanceThreshold, 1f, 10f),
                        showInputField = true,
                    };
                    distSlider.style.width = StyleKeyword.Auto;
                    distSlider.style.flexGrow = 1;
                    distSlider.RegisterValueChangedCallback(evt =>
                    {
                        if (!IsMatrixIndexValid(ia, ib)) return;
                        var e = _simulation.GetGravityEntry(ia, ib);
                        e.DistanceThreshold = Mathf.Clamp(evt.newValue, 1f, 10f);
                        _simulation.SetGravityEntry(ia, ib, e);
                    });
                    advancedContent.Add(distSlider);
                    _distanceSliders[idx] = distSlider;

                    cell.Add(advancedContent);
                    row.Add(cell);
                }

                grid.Add(row);
            }

            // Wrap grid in a ScrollView so the panel header stays fixed while
            // the grid scrolls vertically when there are many particle types.
            var scrollView = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            scrollView.AddToClassList("grid-scroll");
            scrollView.Add(grid);
            _panel.Add(scrollView);

            root.Add(_panel);
        }

        /// <summary>
        /// Creates a runtime font with CJK coverage so UI Toolkit text can render
        /// localized Chinese strings even when project font assets are limited.
        /// </summary>
        private void EnsureRuntimeUiFont()
        {
            if (_runtimeUiFont != null) return;

            string[] preferredFonts =
            {
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "SimHei",
                "Arial Unicode MS",
                "Segoe UI",
            };

            _runtimeUiFont = Font.CreateDynamicFontFromOSFont(preferredFonts, 16);
        }

        // ── Show / Hide / Toggle ──────────────────────────────────────────────

        /// <summary>Shows the matrix config panel. No-op if already open.</summary>
        public void Show()
        {
            if (_isOpen) return;
            BuildUI();
            _isOpen = true;
            _panel.style.display = DisplayStyle.Flex;
            PanelVisibilityChanged?.Invoke(true);
        }

        /// <summary>Hides the matrix config panel. No-op if already closed.</summary>
        public void Hide()
        {
            if (_panel == null) return;
            if (!_isOpen) return;
            _isOpen = false;
            _panel.style.display = DisplayStyle.None;
            PanelVisibilityChanged?.Invoke(false);
        }

        /// <summary>Toggles the matrix config panel open/closed.</summary>
        public void Toggle()
        {
            if (_panel == null) BuildUI();
            if (_isOpen) Hide(); else Show();
        }

        // ── Buttons ───────────────────────────────────────────────────────────

        private void OnRandomize()
        {
            int n   = GetEditableTypeCount();
            var rng = new Unity.Mathematics.Random((uint)System.Environment.TickCount);

            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                int idx = a * n + b;
                float attraction = a == b
                    ? (float)(rng.NextDouble() * 40.0)
                    : (float)(rng.NextDouble() * 80.0 - 40.0);
                var entry = new GravityEntry
                {
                    AttractionStrength = attraction,
                    RepulsionStrength  = (float)(rng.NextDouble() * 20.0 + 2.0),
                    DistanceThreshold  = (float)(rng.NextDouble() * 7.0  + 1.0),
                };
                _simulation.SetGravityEntry(a, b, entry);
                RefreshCell(idx, entry);
            }
        }

        /// <summary>
        /// Resets the entire matrix to the hardcoded defaults (GravityMatrix.CreateDefault).
        /// This triggers a debounced save via ConfigPersistence so the defaults are persisted.
        /// </summary>
        private void OnResetToDefaults()
        {
            _simulation.ResetMatrixToDefault();

            // Refresh all slider visuals to match the new default values.
            int n = GetEditableTypeCount();
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                int idx   = a * n + b;
                var entry = _simulation.GetGravityEntry(a, b);
                RefreshCell(idx, entry);
            }
        }

        // ── Preset section ────────────────────────────────────────────────────

        private VisualElement BuildPresetSection()
        {
            var foldout = new Foldout { text = Localization.Get("preset_section"), value = false };
            foldout.AddToClassList("preset-foldout");

            // Name input row (save button is in the header)
            var inputRow = new VisualElement();
            inputRow.AddToClassList("preset-input-row");
            _presetNameField = new TextField(Localization.Get("preset_name_ph"));
            _presetNameField.AddToClassList("preset-name-field");
            inputRow.Add(_presetNameField);
            foldout.Add(inputRow);

            _presetListContainer = new VisualElement();
            _presetListContainer.AddToClassList("preset-list-container");
            foldout.Add(_presetListContainer);

            RefreshPresetList(PresetPersistence.GetPresets());
            return foldout;
        }

        private void RefreshPresetList(System.Collections.Generic.List<PresetPersistence.PresetEntry> presets = null)
        {
            presets ??= PresetPersistence.GetPresets();
            _presetListContainer.Clear();

            if (_presetSaveButton != null)
                _presetSaveButton.SetEnabled(presets.Count < PresetPersistence.MaxPresets);

            if (presets.Count == 0)
            {
                var empty = new Label(Localization.Get("preset_empty"));
                empty.AddToClassList("preset-empty-label");
                _presetListContainer.Add(empty);
                return;
            }

            int currentTypeCount = _simulation.TypeCount;
            for (int i = 0; i < presets.Count; i++)
            {
                int capturedIndex = i;
                var entry         = presets[i];
                bool compatible   = entry.typeCount == currentTypeCount;

                var row = new VisualElement();
                row.AddToClassList("preset-item-row");

                string displayName = compatible
                    ? entry.name
                    : $"{entry.name}  ({entry.typeCount}型)";
                var nameLabel = new Label(displayName);
                nameLabel.AddToClassList("preset-item-name");
                if (!compatible)
                    nameLabel.style.color = new UnityEngine.Color(0.55f, 0.55f, 0.55f);
                row.Add(nameLabel);

                var loadBtn = new Button(() =>
                {
                    if (!PresetPersistence.TryApplyPreset(entry, _simulation)) return;
                    int n = GetEditableTypeCount();
                    for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++)
                        RefreshCell(a * n + b, _simulation.GetGravityEntry(a, b));
                })
                { text = Localization.Get("preset_load") };
                loadBtn.AddToClassList("preset-item-button");
                loadBtn.SetEnabled(compatible);
                row.Add(loadBtn);

                var deleteBtn = new Button(() =>
                {
                    PresetPersistence.DeletePreset(capturedIndex);
                    RefreshPresetList();
                })
                { text = Localization.Get("preset_delete") };
                deleteBtn.AddToClassList("preset-item-button");
                deleteBtn.AddToClassList("preset-delete-button");
                row.Add(deleteBtn);

                _presetListContainer.Add(row);
            }
        }

        private void OnPresetSave()
        {
            string name = _presetNameField?.value ?? "";
            if (!PresetPersistence.TrySavePreset(name, _simulation)) return;
            if (_presetNameField != null)
                _presetNameField.value = "";
            ShowSaveHint();
            RefreshPresetList();
        }

        // ── Save hint ─────────────────────────────────────────────────────────

        private void ShowSaveHint()
        {
            _wasSaved = true;
            if (_saveHintLabel == null) return;
            _saveHintLabel.text = Localization.Get("preset_saved");
            _saveHintLabel.style.display = DisplayStyle.Flex;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void RefreshCell(int idx, GravityEntry entry)
        {
            float attr = Mathf.Clamp(entry.AttractionStrength, -40f, 40f);
            float rep  = Mathf.Clamp(entry.RepulsionStrength,  0f,   40f);
            float dist = Mathf.Clamp(entry.DistanceThreshold,  1f,   10f);

            if (_attractionSliders[idx] != null)
                _attractionSliders[idx].SetValueWithoutNotify(attr);
            if (_repulsionSliders[idx] != null)
                _repulsionSliders[idx].SetValueWithoutNotify(rep);
            if (_distanceSliders[idx] != null)
                _distanceSliders[idx].SetValueWithoutNotify(dist);

            var cell = _attractionSliders[idx]?.parent;
            if (cell != null)
                UpdateCellColor(cell, attr);
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

        private int GetEditableTypeCount()
        {
            // Matrix UI currently edits configurable normal types only.
            // Clamp with total size for safety against transient size changes.
            return Mathf.Min(_simulation.TypeCount, _simulation.TotalTypeCount);
        }

        private bool IsMatrixIndexValid(int typeA, int typeB)
        {
            int total = _simulation.TotalTypeCount;
            return typeA >= 0 && typeB >= 0 && typeA < total && typeB < total;
        }

        private static bool HasStyleSheet(VisualElement root, StyleSheet styleSheet)
        {
            int count = root.styleSheets.count;
            for (int i = 0; i < count; i++)
            {
                StyleSheet sheet = root.styleSheets[i];
                if (sheet == styleSheet) return true;
            }
            return false;
        }

    }
}
