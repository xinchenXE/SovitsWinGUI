using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoVITS_GUI.Services;

namespace SoVITS_GUI.Pages
{
    public sealed partial class SettingsPage : Page, ISettingsAware
    {
        private readonly MainWindow _mainWindow;
        private bool _loaded = false;
        private readonly List<CondaEnvInfo> _detectedEnvs = new();

        public SettingsPage(MainWindow mainWindow)
        {
            this.InitializeComponent();
            _mainWindow = mainWindow;
            this.Loaded += OnLoaded;
        }

        public void OnSettingsChanged()
        {
            UpdateProjectHint();
        }

        public void OnPaneStateChanged(bool isPaneOpen) { }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;

            var s = _mainWindow.Settings;
            DefaultProjectPathBox.Text = s.DefaultProjectPath ?? "";
            AutoScrollBox.IsChecked = s.AutoScroll;
            ShowTimestampBox.IsChecked = s.ShowTimestamp;

            // 恢复手动指定的 Conda 路径（优先级高于自动检测）
            if (!string.IsNullOrEmpty(s.ManualCondaPath))
            {
                ApplyManualCondaPath(s.ManualCondaPath);
            }

            UpdateProjectHint();
            await DetectCondaEnvironmentsAsync();
        }

        /// <summary>
        /// 根据项目路径下是否存在关键文件，给出路径有效性提示，
        /// 避免用户选错目录后到预处理/训练时才发现问题
        /// </summary>
        private void UpdateProjectHint()
        {
            if (ProjectValidHint == null) return;
            var path = _mainWindow.Settings.ProjectPath ?? "";
            if (string.IsNullOrEmpty(path))
            {
                ProjectValidHint.Text = "尚未设置项目路径";
                return;
            }
            if (!Directory.Exists(path))
            {
                ProjectValidHint.Text = "⚠ 项目路径不存在，请重新选择";
                return;
            }
            var hasInference = File.Exists(Path.Combine(path, "svc_inference.py"));
            var hasConfig = File.Exists(Path.Combine(path, "configs", "base.yaml"));
            ProjectValidHint.Text = hasInference && hasConfig
                ? "✓ 检测到 svc_inference.py 与 configs/base.yaml，项目路径有效"
                : "⚠ 未检测到 svc_inference.py 或 configs/base.yaml，请确认选择的是 So-VITS 项目根目录";
        }
        /// <summary>
        /// 应用手动指定的 Conda 路径到 UI 并同步到设置
        /// </summary>
        private void ApplyManualCondaPath(string path)
        {
            ManualPathText.Text = $"📁 {path}";
            var pythonExe = Path.Combine(path, "python.exe");
            ManualPythonText.Text = File.Exists(pythonExe)
                ? $"🐍 {pythonExe}"
                : "⚠ 未找到 python.exe";

            _mainWindow.Settings.ManualCondaPath = path;
            _mainWindow.Settings.CondaEnvPath = path;
            _mainWindow.Settings.CondaEnvName = Path.GetFileName(path.TrimEnd('\\', '/'));
            _mainWindow.Settings.Save();

            ManualPathPanel.Visibility = Visibility.Visible;
            CondaEnvBox.IsEnabled = false;
            CondaEnvBox.PlaceholderText = "已使用手动路径，自动检测已禁用";
            EnvInfoPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 清除手动指定路径的行为
        /// </summary>
        private void ClearManualSelection()
        {
            _mainWindow.Settings.ManualCondaPath = string.Empty;

            ManualPathPanel.Visibility = Visibility.Collapsed;
            ManualPathText.Text = "";
            ManualPythonText.Text = "";
            CondaEnvBox.IsEnabled = true;

            // 把运行环境同步回自动检测下拉框当前选中的环境：
            // 旧实现只清了 ManualCondaPath，CondaEnvPath 仍指向手动路径，导致"取消"不生效
            if (CondaEnvBox.SelectedItem is ComboBoxItem item && item.Tag is int index
                && index >= 0 && index < _detectedEnvs.Count)
            {
                var env = _detectedEnvs[index];
                _mainWindow.Settings.CondaEnvName = env.Name;
                _mainWindow.Settings.CondaEnvPath = env.Path;
                EnvInfoPanel.Visibility = Visibility.Visible;
                EnvNameText.Text = $"🌟 {env.Name}" + (env.IsActive ? " (当前激活)" : "");
                EnvPathText.Text = $"📁 {env.Path}";
                EnvPythonText.Text = $"🐍 {Path.Combine(env.Path, "python.exe")}";
            }
            else
            {
                // 没有可用的自动检测结果：清空环境配置，退回系统默认 python
                _mainWindow.Settings.CondaEnvName = string.Empty;
                _mainWindow.Settings.CondaEnvPath = string.Empty;
                EnvInfoPanel.Visibility = Visibility.Collapsed;
                CondaEnvBox.PlaceholderText = _detectedEnvs.Count > 0
                    ? $"已检测到 {_detectedEnvs.Count} 个环境，请选择"
                    : "点击右侧按钮检测 Conda 环境...";
            }
            _mainWindow.Settings.Save();
        }

        private async Task DetectCondaEnvironmentsAsync()
        {
            CondaEnvBox.IsEnabled = false;
            RefreshCondaBtn.IsEnabled = false;
            CondaEnvBox.PlaceholderText = "正在检测 Conda 环境...";
            _detectedEnvs.Clear();
            CondaEnvBox.Items.Clear();

            try
            {
                var envs = await Task.Run(() => QueryCondaEnvironments());

                if (envs.Count == 0)
                {
                    CondaEnvBox.PlaceholderText = "未检测到 Conda 环境，将使用系统默认 python";
                    EnvInfoPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _detectedEnvs.AddRange(envs);

                    int selectedIndex = -1;
                    for (int i = 0; i < _detectedEnvs.Count; i++)
                    {
                        var env = _detectedEnvs[i];
                        var marker = env.IsActive ? " ⭐" : "";
                        var label = $"{env.Name}{marker}  ({env.Path})";
                        CondaEnvBox.Items.Add(new ComboBoxItem
                        {
                            Content = label,
                            Tag = i
                        });

                        if (!string.IsNullOrEmpty(_mainWindow.Settings.CondaEnvName)
                            && env.Name == _mainWindow.Settings.CondaEnvName)
                            selectedIndex = i;
                    }

                    if (selectedIndex < 0)
                    {
                        var savedName = _mainWindow.Settings.CondaEnvName;
                        if (!string.IsNullOrEmpty(savedName))
                        {
                            CondaEnvBox.Items.Insert(0, new ComboBoxItem
                            {
                                Content = $"⚠ {savedName} (已失效)",
                                Tag = -1
                            });
                        }
                    }

                    // 恢复上次选择
                    if (selectedIndex >= 0)
                    {
                        CondaEnvBox.SelectedIndex = selectedIndex;
                    }
                    else if (!string.IsNullOrEmpty(_mainWindow.Settings.CondaEnvName))
                    {
                        CondaEnvBox.SelectedIndex = 0; // 显示"已失效"
                    }
                    else
                    {
                        CondaEnvBox.PlaceholderText = $"已检测到 {_detectedEnvs.Count} 个环境，请选择";
                        EnvInfoPanel.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex)
            {
                CondaEnvBox.PlaceholderText = "Conda 检测失败，将使用系统默认 python";
                _mainWindow.AppendLog($"Conda 环境检测失败: {ex.Message}", LogLevel.WARNING);
                EnvInfoPanel.Visibility = Visibility.Collapsed;
            }
            finally
            {
                CondaEnvBox.IsEnabled = true;
                RefreshCondaBtn.IsEnabled = true;
            }
        }

        private static string? FindCondaExe()
        {
            var condaExe = Environment.GetEnvironmentVariable("CONDA_EXE");
            if (!string.IsNullOrEmpty(condaExe) && File.Exists(condaExe))
                return condaExe;

            string[] searchDirs = [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "anaconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "miniconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "anaconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "miniconda3"),
                @"C:\ProgramData\anaconda3",
                @"C:\ProgramData\miniconda3",
            ];

            foreach (var dir in searchDirs)
            {
                var exe = Path.Combine(dir, "Scripts", "conda.exe");
                if (File.Exists(exe)) return exe;
            }

            return null;
        }

        private static List<CondaEnvInfo> QueryCondaEnvironments()
        {
            var envs = new List<CondaEnvInfo>();

            var condaExe = FindCondaExe();
            if (condaExe == null) return envs;

            string condaOutput;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = condaExe,
                        Arguments = "info --envs",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                };
                process.Start();
                condaOutput = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0) return envs;
            }
            catch
            {
                return envs;
            }

            foreach (var line in condaOutput.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                var isActive = trimmed.Contains('*');

                // conda info --envs 输出格式为 "名称 [*] 路径"；
                // 路径可能包含空格，不能简单按空白切分后取最后一个片段
                var firstSpace = trimmed.IndexOf(' ');
                if (firstSpace <= 0) continue;
                var name = trimmed[..firstSpace].TrimEnd('*');
                var path = trimmed[(firstSpace + 1)..].Replace("*", "").Trim();

                if (string.IsNullOrEmpty(path)) continue;

                if (name.Contains('\\') || name.Contains('/'))
                    continue;

                envs.Add(new CondaEnvInfo
                {
                    Name = name,
                    Path = path.Replace("/", "\\"),
                    IsActive = isActive
                });
            }

            return envs;
        }

        private async void RefreshConda_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.AppendLog("重新检测 Conda 环境...", LogLevel.INFO);
            await DetectCondaEnvironmentsAsync();
        }

        /// <summary>
        /// 手动选择 Conda 环境路径（优先于自动检测）
        /// </summary>
        private async void BrowseConda_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            Services.WindowHelper.InitializePickerWithWindow(picker, _mainWindow);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                _mainWindow.AppendLog($"已手动选择 Conda 环境: {folder.Path}", LogLevel.INFO);
                ApplyManualCondaPath(folder.Path);
            }
        }

        /// <summary>
        /// 取消手动选择，恢复自动检测模式
        /// </summary>
        private void ClearManual_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.AppendLog("已取消手动选择，恢复自动检测模式", LogLevel.INFO);
            ClearManualSelection();
        }

        private void CondaEnvBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;

            if (CondaEnvBox.SelectedItem is ComboBoxItem item && item.Tag is int index)
            {
                if (index >= 0 && index < _detectedEnvs.Count)
                {
                    var env = _detectedEnvs[index];
                    _mainWindow.Settings.CondaEnvName = env.Name;
                    _mainWindow.Settings.CondaEnvPath = env.Path;
                    _mainWindow.Settings.Save();

                    EnvInfoPanel.Visibility = Visibility.Visible;
                    EnvNameText.Text = $"🌟 {env.Name}" + (env.IsActive ? " (当前激活)" : "");
                    EnvPathText.Text = $"📁 {env.Path}";
                    EnvPythonText.Text = $"🐍 {Path.Combine(env.Path, "python.exe")}";

                    _mainWindow.AppendLog($"Conda 环境已切换至: {env.Name} ({env.Path})", LogLevel.SUCCESS);
                }
            }
        }

        private void AutoScrollBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _mainWindow.Settings.AutoScroll = AutoScrollBox.IsChecked == true;
            _mainWindow.Settings.Save();
        }

        private void ShowTimestampBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _mainWindow.Settings.ShowTimestamp = ShowTimestampBox.IsChecked == true;
            _mainWindow.Settings.Save();
        }

        private async void BrowseDefaultProject_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");

            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                DefaultProjectPathBox.Text = folder.Path;
                _mainWindow.Settings.DefaultProjectPath = folder.Path;
                _mainWindow.Settings.ProjectPath = folder.Path;
                _mainWindow.Settings.Save();
                _mainWindow.AppendLog($"默认项目路径已设置为: {folder.Path}", LogLevel.INFO);
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.ClearLogs();
            _mainWindow.AppendLog("日志已清空", LogLevel.INFO);
        }

        private async void SaveLog_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.FileTypeChoices.Add("文本文件", new[] { ".txt" });
            picker.FileTypeChoices.Add("日志文件", new[] { ".log" });
            picker.SuggestedFileName = $"SoVITS_Log_{DateTime.Now:yyyyMMdd_HHmmss}";

            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                _mainWindow.AppendLog($"正在导出日志到: {file.Path}", LogLevel.INFO);
                var logContent = _mainWindow.GetLogText();
                await Windows.Storage.FileIO.WriteTextAsync(file, logContent);
                _mainWindow.AppendLog("日志导出成功", LogLevel.SUCCESS);
            }
        }
    }

    internal class CondaEnvInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }
}
