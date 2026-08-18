using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SoVITS_GUI.Services;

namespace SoVITS_GUI.Pages
{
    public sealed partial class PreprocessPage : Page, ISettingsAware
    {
        private readonly MainWindow _mainWindow;
        private bool _isMyProcess;

        public PreprocessPage(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            this.InitializeComponent();

            ThreadSlider.ValueChanged += ThreadSlider_ValueChanged;
            F0ExtractorBox.SelectionChanged += F0ExtractorBox_SelectionChanged;
            WhisperVersionBox.SelectionChanged += WhisperVersionBox_SelectionChanged;

            _mainWindow.ProcessService.ProcessExited += OnProcessExited;

            OnSettingsChanged();
            RestoreFromSettings();
        }

        /// <summary>
        /// 从持久化设置恢复控件状态（F0 提取器、Whisper 版本、线程数）
        /// </summary>
        private void RestoreFromSettings()
        {
            var s = _mainWindow.Settings;
            F0ExtractorBox.SelectedIndex = s.F0Extractor == "rmvpe" ? 1 : 0;
            WhisperVersionBox.SelectedIndex = s.WhisperVersion == "large-v2" ? 1 : 0;
            var threads = Math.Clamp(s.PreprocessThreads, 1, (int)ThreadSlider.Maximum);
            ThreadSlider.Value = threads;
            ThreadLabel.Text = $"当前：{threads} 线程";
        }

        private string CurrentF0Extractor => F0ExtractorBox.SelectedIndex == 1 ? "rmvpe" : "crepe";
        private string CurrentWhisperVersion => WhisperVersionBox.SelectedIndex == 1 ? "large-v2" : "large-v3";

        /// <summary>
        /// 工作目录：优先使用用户配置的项目路径
        /// </summary>
        private string WorkDir => string.IsNullOrEmpty(_mainWindow.Settings.ProjectPath)
            ? AppDomain.CurrentDomain.BaseDirectory
            : _mainWindow.Settings.ProjectPath;

        private void OnProcessExited(object? sender, int exitCode)
        {
            if (!_isMyProcess) return;
            _isMyProcess = false;
            StartPreprocessBtn.IsEnabled = true;
            StopPreprocessBtn.IsEnabled = false;
        }

        public void OnSettingsChanged()
        {
            var path = _mainWindow.Settings.ProjectPath ?? "";
            ProjectPathLabel.Text = string.IsNullOrEmpty(path)
                ? "" : path;
        }

        public void OnPaneStateChanged(bool isPaneOpen) { }

        private void ThreadSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (ThreadLabel != null)
                ThreadLabel.Text = $"当前：{e.NewValue:F0} 线程";
        }

        private void F0ExtractorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _mainWindow.Settings.F0Extractor = CurrentF0Extractor;
            _mainWindow.Settings.Save();
        }

        private void WhisperVersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _mainWindow.Settings.WhisperVersion = CurrentWhisperVersion;
            _mainWindow.Settings.Save();
        }

        /// <summary>
        /// 启动前预检：数据集与各阶段所需预训练模型是否齐备
        /// </summary>
        private bool ValidatePrerequisites(string f0Extractor, string whisperVersion)
        {
            var root = WorkDir;
            var ok = true;

            // 数据集：dataset_raw/<说话人>/*.wav
            var datasetDir = Path.Combine(root, "dataset_raw");
            if (!Directory.Exists(datasetDir))
            {
                _mainWindow.AppendLog($"数据集目录不存在: {datasetDir}", LogLevel.ERROR);
                return false;
            }
            var wavCount = Directory.EnumerateFiles(datasetDir, "*.wav", SearchOption.AllDirectories).Count();
            if (wavCount == 0)
            {
                _mainWindow.AppendLog("dataset_raw 中未找到 wav 文件，请先按说话人目录放置数据", LogLevel.ERROR);
                return false;
            }
            _mainWindow.AppendLog($"数据集检查通过: 共 {wavCount} 个 wav 文件", LogLevel.INFO);

            // 各阶段依赖的预训练模型
            var required = new List<(string file, string desc)>
            {
                (Path.Combine(root, "whisper_pretrain", whisperVersion == "large-v3" ? "large-v3.pt" : "large-v2.pt"),
                    $"Whisper 预训练模型 ({whisperVersion})"),
                (Path.Combine(root, "hubert_pretrain", "hubert-soft-0d54a1f4.pt"), "HuBERT-soft 预训练模型"),
                (Path.Combine(root, "speaker_pretrain", "best_model.pth.tar"), "说话人编码器预训练模型"),
            };
            required.Add(f0Extractor == "rmvpe"
                ? (Path.Combine(root, "pretrain", "rmvpe.pt"), "RMVPE 音高提取器预训练模型")
                : (Path.Combine(root, "crepe", "assets", "full.pth"), "CREPE 音高提取器预训练模型"));

            foreach (var (file, desc) in required)
            {
                if (!File.Exists(file))
                {
                    _mainWindow.AppendLog($"缺少{desc}: {file}", LogLevel.ERROR);
                    ok = false;
                }
            }
            return ok;
        }

        /// <summary>
        /// 一键预处理：按 F0 提取器 × Whisper 版本选择对应的编排脚本
        /// </summary>
        private void StartPreprocess_Click(object sender, RoutedEventArgs e)
        {
            var threads = (int)ThreadSlider.Value;
            var f0 = CurrentF0Extractor;
            var whisper = CurrentWhisperVersion;

            // 本项目为 LargeV3-MiX3，默认走 v3 PPG 提取；v2 仅用于兼容旧底模
            var script = (f0, whisper) switch
            {
                ("crepe", "large-v3") => "svc_preprocessingV3.py",
                ("crepe", _)          => "svc_preprocessingV2.py",
                ("rmvpe", "large-v3") => "svc_preprocessingrmvpeV3.py",
                _                     => "svc_preprocessingrmvpeV2.py",
            };

            if (!ValidatePrerequisites(f0, whisper))
            {
                _mainWindow.AppendLog("预检查未通过，请补齐上述文件后重试", LogLevel.ERROR);
                return;
            }

            var args = $"-t {threads}";

            _mainWindow.AppendLog($"启动预处理: 脚本={script}, F0提取器={f0}, Whisper={whisper}, 线程数={threads}", LogLevel.INFO);
            _mainWindow.UpdateStatus("预处理中...");

            _isMyProcess = true;
            StartPreprocessBtn.IsEnabled = false;
            StopPreprocessBtn.IsEnabled = true;

            _ = _mainWindow.ProcessService.StartScriptAsync(
                _mainWindow.ResolvedPythonPath,
                script,
                args,
                WorkDir);
        }

        /// <summary>
        /// 停止预处理进程
        /// </summary>
        private void StopPreprocess_Click(object sender, RoutedEventArgs e)
        {
            _isMyProcess = false;
            _mainWindow.ProcessService.StopProcess();
            StartPreprocessBtn.IsEnabled = true;
            StopPreprocessBtn.IsEnabled = false;
            _mainWindow.UpdateStatus("");
        }
    }
}
