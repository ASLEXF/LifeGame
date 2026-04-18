using System;
using System.Collections.Generic;
using System.IO;
using ParticleLife.Core;
using ParticleLife.Management;
using ParticleLife.Simulation;
using UnityEngine;

namespace ParticleLife.Persistence
{
    /// <summary>
    /// Manages up to <see cref="MaxPresets"/> named matrix presets stored as JSON
    /// alongside matrix_config.json.
    /// All methods are synchronous main-thread file I/O.
    /// </summary>
    public static class PresetPersistence
    {
        public const int MaxPresets = 10;

        private const string FileName = "matrix_presets.json";

        private static string SavePath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath)!, FileName);

        [Serializable]
        public class PresetEntry
        {
            public string  name;
            public int     typeCount;
            public float[] attractionStrengths;
            public float[] repulsionStrengths;
            public float[] distanceThresholds;
        }

        [Serializable]
        private class PresetData
        {
            public List<PresetEntry> presets = new List<PresetEntry>();
        }

        /// <summary>Returns all saved presets. Empty list if file is missing or corrupt.</summary>
        public static List<PresetEntry> GetPresets()
        {
            if (!File.Exists(SavePath)) return new List<PresetEntry>();
            try
            {
                var data = JsonUtility.FromJson<PresetData>(File.ReadAllText(SavePath));
                return data?.presets ?? new List<PresetEntry>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PresetPersistence] 读取失败: {ex.Message}");
                return new List<PresetEntry>();
            }
        }

        /// <summary>
        /// Saves the current matrix as a new preset.
        /// Returns false if already at <see cref="MaxPresets"/> capacity.
        /// </summary>
        public static bool TrySavePreset(string name, ParticleSimulation sim)
        {
            var presets = GetPresets();
            if (presets.Count >= MaxPresets) return false;

            int n    = sim.TypeCount;
            int size = n * n;
            var entry = new PresetEntry
            {
                name                = string.IsNullOrWhiteSpace(name) ? string.Format(Localization.Get("preset_default_name"), presets.Count + 1) : name,
                typeCount           = n,
                attractionStrengths = new float[size],
                repulsionStrengths  = new float[size],
                distanceThresholds  = new float[size],
            };

            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                int idx = a * n + b;
                GravityEntry e = sim.GetGravityEntry(a, b);
                entry.attractionStrengths[idx] = e.AttractionStrength;
                entry.repulsionStrengths [idx] = e.RepulsionStrength;
                entry.distanceThresholds [idx] = e.DistanceThreshold;
            }

            presets.Add(entry);
            WriteAll(presets);
            return true;
        }

        /// <summary>
        /// Applies the preset's values to the simulation matrix.
        /// Returns false if the preset's typeCount doesn't match the simulation's current TypeCount.
        /// </summary>
        public static bool TryApplyPreset(PresetEntry entry, ParticleSimulation sim)
        {
            if (entry.typeCount != sim.TypeCount) return false;

            int n = entry.typeCount;
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                int idx = a * n + b;
                sim.SetGravityEntry(a, b, new GravityEntry
                {
                    AttractionStrength = entry.attractionStrengths[idx],
                    RepulsionStrength  = entry.repulsionStrengths [idx],
                    DistanceThreshold  = entry.distanceThresholds [idx],
                });
            }
            return true;
        }

        /// <summary>Removes the preset at <paramref name="index"/> and persists the change.</summary>
        public static void DeletePreset(int index)
        {
            var presets = GetPresets();
            if ((uint)index >= (uint)presets.Count) return;
            presets.RemoveAt(index);
            WriteAll(presets);
        }

        private static void WriteAll(List<PresetEntry> presets)
        {
            try
            {
                string json = JsonUtility.ToJson(new PresetData { presets = presets }, prettyPrint: true);
                File.WriteAllText(SavePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PresetPersistence] 保存失败: {ex.Message}");
            }
        }
    }
}
