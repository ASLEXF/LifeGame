using System;
using System.Collections.Generic;

namespace ParticleLife.Management
{
    /// <summary>
    /// Lightweight static localization service supporting Chinese and English.
    ///
    /// Usage:
    ///   Localization.SetLanguage(Localization.Language.English);
    ///   string s = Localization.Get("start");
    ///   string t = Localization.FormatTime(seconds);
    ///
    /// Subscribe to <see cref="OnLanguageChanged"/> to update UI whenever the
    /// active language changes. Default language is Chinese.
    /// </summary>
    public static class Localization
    {
        public enum Language { Chinese, English }

        /// <summary>Currently active language. Default: Chinese.</summary>
        public static Language Current { get; private set; } = Language.Chinese;

        /// <summary>Fired whenever <see cref="SetLanguage"/> changes the active language.</summary>
        public static event Action<Language> OnLanguageChanged;

        public static void SetLanguage(Language lang)
        {
            if (Current == lang) return;
            Current = lang;
            OnLanguageChanged?.Invoke(lang);
        }

        /// <summary>
        /// Returns the localized string for <paramref name="key"/> in the current language.
        /// Falls back to <paramref name="key"/> itself if no entry exists.
        /// </summary>
        public static string Get(string key)
        {
            if (_table.TryGetValue(Current, out var dict) && dict.TryGetValue(key, out string val))
                return val;
            return key;
        }

        /// <summary>Formats a duration using the current language's time pattern.</summary>
        public static string FormatTime(float seconds)
        {
            int m = (int)(seconds / 60);
            int s = (int)(seconds % 60);
            return m > 0
                ? string.Format(Get("time_format_ms"), m, s)
                : string.Format(Get("time_format_s"), s);
        }

        // ── String table ──────────────────────────────────────────────────────

        private static readonly Dictionary<Language, Dictionary<string, string>> _table =
            new Dictionary<Language, Dictionary<string, string>>
            {
                [Language.Chinese] = new Dictionary<string, string>
                {
                    // ── Main Menu ──────────────────────────────────────
                    ["title"]            = "粒子生命",
                    ["start"]            = "开始游戏",
                    ["config"]           = "配置引力矩阵",
                    ["hint_keyboard"]    = "按 Enter / Space 开始游戏",
                    ["lang_toggle"]      = "EN",

                    // ── HUD ────────────────────────────────────────────
                    ["hud_player_count"] = "玩家粒子数：{0}",
                    ["hud_total_count"]  = "粒子总数：{0}",
                    ["hud_survival"]     = "存活：{0}",

                    // ── Failure Screen ─────────────────────────────────
                    ["fail_title"]       = "你被捕获了",
                    ["fail_survival"]    = "存活时间：{0}",
                    ["fail_peak"]        = "峰值粒子数：{0}",
                    ["fail_restart"]     = "重新开始  [R]",

                    // ── Time formatting ────────────────────────────────
                    ["time_format_ms"]   = "{0}分{1:D2}秒",
                    ["time_format_s"]    = "{0}秒",

                    // ── Matrix Config ──────────────────────────────────
                    ["matrix_title"]         = "引力矩阵配置",
                    ["matrix_randomize"]     = "随机化",
                    ["matrix_reset_def"]     = "重置为默认",
                    ["matrix_hint"]          = "行 = 施力方，列 = 受力方",
                    ["matrix_type"]          = "类型 {0}",
                    ["matrix_attraction"]    = "引力",
                    ["matrix_advanced"]      = "高级",
                    ["matrix_repulsion"]     = "斥力",
                    ["matrix_distance"]      = "距离阈值",
                    ["matrix_tip_randomize"] = "随机生成全部引力与斥力值",
                    ["matrix_tip_reset_def"] = "还原为程序内置默认值（会自动保存）",
                    ["matrix_keyboard_hint"] = "[Tab] 切换面板    [Esc] 关闭",
                    ["matrix_config_loaded"] = "已自动读取 matrix_config.json",
                    ["matrix_save_hint"]     = "已保存修改到 matrix_config.json",

                    // ── Preset Management ──────────────────────────────
                    ["preset_section"]   = "预设管理",
                    ["preset_default_name"] = "预设 {0}",

                    ["preset_save"]      = "保存预设",
                    ["preset_load"]      = "加载",
                    ["preset_delete"]    = "删除",
                    ["preset_empty"]     = "暂无预设",
                    ["preset_saved"]     = "预设已保存",
                },
                [Language.English] = new Dictionary<string, string>
                {
                    // ── Main Menu ──────────────────────────────────────
                    ["title"]            = "Particle Life Game",
                    ["start"]            = "Start Game",
                    ["config"]           = "Configure Matrix",
                    ["hint_keyboard"]    = "Press Enter / Space to Start",
                    ["lang_toggle"]      = "中文",

                    // ── HUD ────────────────────────────────────────────
                    ["hud_player_count"] = "Player: {0}",
                    ["hud_total_count"]  = "Total: {0}",
                    ["hud_survival"]     = "Survived: {0}",

                    // ── Failure Screen ─────────────────────────────────
                    ["fail_title"]       = "You've Been Captured",
                    ["fail_survival"]    = "Survived: {0}",
                    ["fail_peak"]        = "Peak: {0}",
                    ["fail_restart"]     = "Restart  [R]",

                    // ── Time formatting ────────────────────────────────
                    ["time_format_ms"]   = "{0}m {1:D2}s",
                    ["time_format_s"]    = "{0}s",

                    // ── Matrix Config ──────────────────────────────────
                    ["matrix_title"]         = "Gravity Matrix",
                    ["matrix_randomize"]     = "Randomize",
                    ["matrix_reset_def"]     = "Reset to Default",
                    ["matrix_hint"]          = "Row = Attractor,  Col = Receiver",
                    ["matrix_type"]          = "Type {0}",
                    ["matrix_attraction"]    = "Attraction",
                    ["matrix_advanced"]      = "Advanced",
                    ["matrix_repulsion"]     = "Repulsion",
                    ["matrix_distance"]      = "Dist. Threshold",
                    ["matrix_tip_randomize"] = "Randomize all attraction and repulsion values",
                    ["matrix_tip_reset_def"] = "Restore built-in defaults (auto-saved)",
                    ["matrix_keyboard_hint"] = "[Tab] Toggle Panel    [Esc] Close",
                    ["matrix_config_loaded"] = "Loaded matrix_config.json",
                    ["matrix_save_hint"]     = "Saved to matrix_config.json",

                    // ── Preset Management ──────────────────────────────
                    ["preset_section"]   = "Presets",
                    ["preset_default_name"] = "Preset {0}",

                    ["preset_save"]      = "Save Preset",
                    ["preset_load"]      = "Load",
                    ["preset_delete"]    = "Del",
                    ["preset_empty"]     = "No presets saved",
                    ["preset_saved"]     = "Preset Saved",
                },
            };
    }
}
