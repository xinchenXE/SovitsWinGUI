using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SoVITS_GUI.Models
{
    public class AppConfig
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoVITS_GUI");
        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        public string ProjectPath { get; set; } = string.Empty;
        public string CondaEnvName { get; set; } = string.Empty;
        public string CondaEnvPath { get; set; } = string.Empty;
        /// <summary>手动指定的 Conda 环境路径，非空时优先于自动检测</summary>
        public string ManualCondaPath { get; set; } = string.Empty;
        public string DefaultProjectPath { get; set; } = string.Empty;
        public bool AutoScroll { get; set; } = true;
        public bool ShowTimestamp { get; set; } = true;
        public int PreprocessThreads { get; set; } = 2;
        public string F0Extractor { get; set; } = "crepe";
        // 本项目为 LargeV3-MiX3，默认使用 large-v3（旧安装已保存的值不受影响）
        public string WhisperVersion { get; set; } = "large-v3";
        public string ModelName { get; set; } = "sovits5.0";
        public double LearningRate { get; set; } = 5e-5;
        public int BatchSize { get; set; } = 6;
        public bool UsePretrain { get; set; } = true;
        public int PitchShift { get; set; } = 0;
        public string SpeakerFile { get; set; } = string.Empty;
        /// <summary>推理页使用的角色 .npy 文件所在目录</summary>
        public string SpeakerDir { get; set; } = string.Empty;
        public string InputAudioPath { get; set; } = string.Empty;

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppConfig.Load failed: {ex.Message}");
            }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppConfig.Save failed: {ex.Message}");
            }
        }
    }
}
