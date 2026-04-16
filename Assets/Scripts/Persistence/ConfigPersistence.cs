using System;
using System.IO;
using ParticleLife.Core;
using ParticleLife.Simulation;
using UnityEngine;

namespace ParticleLife.Persistence
{
    /// <summary>
    /// Persists the gravity matrix to disk as JSON.
    ///
    /// Behavior:
    ///   Load  — on Start(), reads Application.persistentDataPath/matrix_config.json.
    ///           If file is missing or corrupt, silently keeps the default matrix.
    ///   Save  — debounced 2s after any SetGravityEntry() call.
    ///           Triggered via ParticleSimulation.OnGravityMatrixChanged.
    ///
    /// Setup (Unity Editor):
    ///   Attach to any persistent GameObject (e.g. GameManager).
    ///   Assign _simulation reference.
    /// </summary>
    public class ConfigPersistence : MonoBehaviour
    {
        [Header("引用")]
        [SerializeField] private ParticleSimulation _simulation;

        [Header("设置")]
        [Tooltip("最后一次矩阵变更到自动保存的等待秒数")]
        [SerializeField] private float _saveDebounceSeconds = 2f;

        private const string FileName = "matrix_config.json";

        private float _saveTimer = -1f; // -1 = not pending

        /// <summary>True if matrix_config.json was successfully loaded from disk on session start.</summary>
        public bool WasLoadedFromDisk { get; private set; }

        /// <summary>
        /// Editor  : &lt;ProjectRoot&gt;/matrix_config.json  (one level above Assets/)
        /// Build   : &lt;GameFolder&gt;/matrix_config.json   (same directory as the .exe)
        /// </summary>
        private static string SavePath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath)!, FileName);

        // ── Serialization container ───────────────────────────────────────────

        [Serializable]
        private class MatrixData
        {
            public int       typeCount;
            public float[]   attractionStrengths;
            public float[]   repulsionStrengths;
            public float[]   distanceThresholds;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Start()
        {
            LoadFromDisk();
            _simulation.OnGravityMatrixChanged += OnMatrixChanged;
        }


        private void OnDestroy()
        {
            if (_simulation != null)
                _simulation.OnGravityMatrixChanged -= OnMatrixChanged;

            // Flush any pending save immediately on shutdown
            if (_saveTimer >= 0f)
                SaveToDisk();
        }

        private void Update()
        {
            if (_saveTimer < 0f) return;

            _saveTimer -= Time.deltaTime;
            if (_saveTimer <= 0f)
            {
                _saveTimer = -1f;
                SaveToDisk();
            }
        }

        // ── Event handler ─────────────────────────────────────────────────────

        private void OnMatrixChanged()
        {
            // Reset debounce timer every time a change arrives
            _saveTimer = _saveDebounceSeconds;
        }

        // ── IO ────────────────────────────────────────────────────────────────

        private void SaveToDisk()
        {
            int n    = _simulation.TypeCount;
            int size = n * n;

            var data = new MatrixData
            {
                typeCount           = n,
                attractionStrengths = new float[size],
                repulsionStrengths  = new float[size],
                distanceThresholds  = new float[size],
            };

            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                int idx = a * n + b;
                GravityEntry e = _simulation.GetGravityEntry(a, b);
                data.attractionStrengths[idx] = e.AttractionStrength;
                data.repulsionStrengths [idx] = e.RepulsionStrength;
                data.distanceThresholds [idx] = e.DistanceThreshold;
            }

            try
            {
                string json = JsonUtility.ToJson(data, prettyPrint: true);
                File.WriteAllText(SavePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ConfigPersistence] 保存失败: {ex.Message}");
            }
        }

        private void LoadFromDisk()
        {
            if (!File.Exists(SavePath)) return;

            MatrixData data;
            try
            {
                string json = File.ReadAllText(SavePath);
                data = JsonUtility.FromJson<MatrixData>(json);

                if (data == null || data.typeCount != _simulation.TypeCount)
                    throw new Exception("类型数量不匹配或数据为空");

                int n    = data.typeCount;
                int size = n * n;
                if (data.attractionStrengths?.Length != size ||
                    data.repulsionStrengths?.Length  != size ||
                    data.distanceThresholds?.Length  != size)
                    throw new Exception("数组长度不一致");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ConfigPersistence] 加载失败，使用默认配置: {ex.Message}");
                return; // Keep simulation defaults
            }

            // Apply — suppress save triggered by these SetGravityEntry calls
            _saveTimer = -1f;
            _simulation.OnGravityMatrixChanged -= OnMatrixChanged;

            int typeCount = data.typeCount;
            for (int a = 0; a < typeCount; a++)
            for (int b = 0; b < typeCount; b++)
            {
                int idx = a * typeCount + b;
                _simulation.SetGravityEntry(a, b, new GravityEntry
                {
                    AttractionStrength = data.attractionStrengths[idx],
                    RepulsionStrength  = data.repulsionStrengths [idx],
                    DistanceThreshold  = data.distanceThresholds [idx],
                });
            }

            _simulation.OnGravityMatrixChanged += OnMatrixChanged;
            WasLoadedFromDisk = true;
        }
    }
}
