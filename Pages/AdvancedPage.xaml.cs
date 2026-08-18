using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoVITS_GUI.Services;

namespace SoVITS_GUI.Pages
{
    public sealed partial class AdvancedPage : Page, ISettingsAware
    {
        /// <summary>
        /// 音色混合列表中的一条记录：嵌入文件路径 + 权重输入框
        /// </summary>
        private sealed class MixEntry
        {
            public string Path { get; init; } = "";
            public TextBox WeightBox { get; init; } = null!;
            public FrameworkElement Row { get; init; } = null!;
        }

        private readonly MainWindow _mainWindow;
        private bool _isMyProcess;
        private string _testAudioPath = "";
        // 记录最近一次通过子进程执行的工具，进程退出后校验其产物
        private string _lastTool = "";
        // 本次执行使用的临时批处理脚本（特征提取测试生成），进程退出后删除
        private string? _pendingBatPath;
        private readonly List<MixEntry> _mixEntries = new();

        public AdvancedPage(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            this.InitializeComponent();
            _mainWindow.ProcessService.ProcessExited += OnProcessExited;
            OnSettingsChanged();

            // 清理历史遗留的临时批处理脚本（尽力而为）
            TempBatCleanup.PurgeStale("sovits_adv_");
        }

        public void OnSettingsChanged()
        {
            var path = _mainWindow.Settings.ProjectPath ?? "";
            ProjectPathLabel.Text = string.IsNullOrEmpty(path)
                ? "" : path;
        }

        public void OnPaneStateChanged(bool isPaneOpen) { }

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
            // 删除本次使用的临时批处理脚本，避免在 %TEMP% 累积
            TempBatCleanup.TryDelete(_pendingBatPath);
            _pendingBatPath = null;
            SetRunningState(false);
            _mainWindow.UpdateStatus("");
            if (exitCode == 0)
                VerifyToolOutput();
        }

        private void SetRunningState(bool running)
        {
            ExportModelBtn.IsEnabled = !running;
            BuildIndexBtn.IsEnabled = !running;
            TestDataLoaderBtn.IsEnabled = !running;
            FeatureExtractBtn.IsEnabled = !running;
            StopBtn.IsEnabled = running;
            StatusLabel.Text = running ? "运行中..." : "就绪";
        }

        /// <summary>
        /// 启动一个依赖 Python 子进程的工具，并记录工具类型以便退出后校验产物
        /// </summary>
        private void StartTool(string toolKey, string toolName, string exe, string script, string args, string? batPath = null)
        {
            _isMyProcess = true;
            _lastTool = toolKey;
            _pendingBatPath = batPath;
            SetRunningState(true);
            _mainWindow.UpdateStatus($"{toolName}中...");
            _mainWindow.AppendLog($"开始{toolName}...", LogLevel.INFO);

            _ = _mainWindow.ProcessService.StartScriptAsync(exe, script, args, WorkDir);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _isMyProcess = false;
            _lastTool = "";
            _mainWindow.ProcessService.StopProcess();
            SetRunningState(false);
            _mainWindow.UpdateStatus("");
        }

        /// <summary>
        /// 进程成功退出后，按工具类型检查预期产物是否生成
        /// </summary>
        private void VerifyToolOutput()
        {
            switch (_lastTool)
            {
                case "export":
                    var exported = Path.Combine(WorkDir, "sovits5.0.pth");
                    if (File.Exists(exported))
                        _mainWindow.AppendLog($"导出成功: {exported}（同时生成 sovits5.0.pretrain.pth 可用于继续微调）", LogLevel.SUCCESS);
                    else
                        _mainWindow.AppendLog("未在目录中找到 sovits5.0.pth，请检查上方日志确认导出是否成功", LogLevel.WARNING);
                    break;

                case "index":
                    var indexRoot = Path.Combine(WorkDir, "data_svc", "indexes");
                    if (Directory.Exists(indexRoot))
                    {
                        var count = Directory.EnumerateFiles(indexRoot, "*.index", SearchOption.AllDirectories).Count();
                        _mainWindow.AppendLog($"索引构建完成，共 {count} 个 .index 文件（{indexRoot}）", LogLevel.SUCCESS);
                    }
                    else
                    {
                        _mainWindow.AppendLog("未找到 data_svc/indexes 目录，请检查上方日志", LogLevel.WARNING);
                    }
                    break;

                case "feature-test":
                    string[] outputs = { "svc_tmp.ppg.npy", "svc_tmp.vec.npy", "svc_tmp.pit.csv" };
                    foreach (var name in outputs)
                    {
                        var f = Path.Combine(WorkDir, name);
                        if (File.Exists(f))
                            _mainWindow.AppendLog($"已生成: {f}（{new FileInfo(f).Length} 字节）", LogLevel.SUCCESS);
                        else
                            _mainWindow.AppendLog($"未生成: {f}，请检查对应提取步骤的日志", LogLevel.WARNING);
                    }
                    break;

                default:
                    break;
            }
            _lastTool = "";
        }

        // ==================== 模型导出 ====================

        private async void BrowseCkpt_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".pt");
            picker.FileTypeFilter.Add(".pth");

            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                CheckpointPathBox.Text = file.Path;
                _mainWindow.AppendLog($"已选择检查点: {file.Path}", LogLevel.INFO);
            }
        }

        private void ExportModel_Click(object sender, RoutedEventArgs e)
        {
            var checkpointPath = CheckpointPathBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(checkpointPath))
            {
                _mainWindow.AppendLog("请先选择模型检查点文件", LogLevel.WARNING);
                return;
            }
            if (!File.Exists(checkpointPath))
            {
                _mainWindow.AppendLog($"检查点文件不存在: {checkpointPath}", LogLevel.ERROR);
                return;
            }

            // 使用绝对配置路径，避免依赖相对路径在不同工作目录下失效
            var configPath = Path.Combine(WorkDir, "configs", "base.yaml");
            if (!File.Exists(configPath))
            {
                _mainWindow.AppendLog($"未找到配置文件: {configPath}", LogLevel.ERROR);
                return;
            }

            StartTool("export", "导出模型",
                _mainWindow.ResolvedPythonPath, "svc_export.py",
                $"--config \"{configPath}\" --checkpoint_path \"{checkpointPath}\"");
        }

        // ==================== 音色混合（原生实现） ====================

        /// <summary>
        /// 选择参与混合的音色嵌入文件（可多选 .npy）
        /// </summary>
        private async void AddMixFiles_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".npy");
            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;

            foreach (var file in files)
            {
                if (_mixEntries.Any(m => string.Equals(m.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                AddMixRow(file.Path);
            }
            _mainWindow.AppendLog($"已添加 {files.Count} 个音色文件，当前共 {_mixEntries.Count} 个", LogLevel.INFO);
        }

        private void AddMixRow(string path)
        {
            var nameText = new TextBlock
            {
                Text = Path.GetFileName(path),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["TextPrimaryBrush"],
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
            };
            ToolTipService.SetToolTip(nameText, path);

            var weightBox = new TextBox
            {
                Text = "0.5",
                Width = 80,
                FontSize = 12,
            };
            ToolTipService.SetToolTip(weightBox, "混合权重（0~1 之间的小数）");

            var removeBtn = new Button
            {
                Content = "移除",
                FontSize = 12,
                Style = (Style)Application.Current.Resources["SecondaryButtonStyle"],
                MinWidth = 60,
                Padding = new Thickness(10, 4, 10, 4),
            };

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(nameText);
            row.Children.Add(weightBox);
            row.Children.Add(removeBtn);
            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(weightBox, 1);
            Grid.SetColumn(removeBtn, 2);

            var entry = new MixEntry { Path = path, WeightBox = weightBox, Row = row };
            removeBtn.Click += (_, _) =>
            {
                _mixEntries.Remove(entry);
                VoiceMixPanel.Children.Remove(row);
            };

            _mixEntries.Add(entry);
            VoiceMixPanel.Children.Add(row);
        }

        /// <summary>
        /// 执行音色混合：读取各 .npy 嵌入做加权求和，保存到 data_svc/singer/&lt;名称&gt;.spk.npy。
        /// 旧实现调用 svc_eva.py，但混合比例硬编码在脚本内部，界面上无法配置；现改为 C# 原生计算
        /// </summary>
        private void MixVoice_Click(object sender, RoutedEventArgs e)
        {
            if (_mixEntries.Count == 0)
            {
                _mainWindow.AppendLog("请先添加至少一个音色文件（.npy）", LogLevel.WARNING);
                return;
            }

            // 输出名称
            var outName = (MixOutputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(outName)) outName = "eva";
            var invalid = Path.GetInvalidFileNameChars();
            outName = new string(outName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

            try
            {
                // 解析权重并加载嵌入
                var vectors = new List<(string path, double weight, double[] data)>();
                foreach (var entry in _mixEntries)
                {
                    if (!double.TryParse(entry.WeightBox.Text?.Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var weight))
                    {
                        _mainWindow.AppendLog($"权重格式无效: {Path.GetFileName(entry.Path)}（{entry.WeightBox.Text}）", LogLevel.ERROR);
                        return;
                    }
                    var data = NpyFile.ReadVector(entry.Path);
                    vectors.Add((entry.Path, weight, data));
                }

                // 校验维度一致、权重非全零
                var dim = vectors[0].data.Length;
                if (vectors.Any(v => v.data.Length != dim))
                {
                    _mainWindow.AppendLog("音色嵌入维度不一致，无法混合（不同来源的 .npy 混在一起了？）", LogLevel.ERROR);
                    return;
                }
                if (vectors.All(v => v.weight == 0))
                {
                    _mainWindow.AppendLog("所有权重都为 0，混合结果将是零向量，请调整权重", LogLevel.ERROR);
                    return;
                }

                // 加权求和（与 svc_eva.py 的算法一致）
                var mixed = new double[dim];
                foreach (var (_, weight, data) in vectors)
                    for (int i = 0; i < dim; i++)
                        mixed[i] += data[i] * weight;

                // 保存到 data_svc/singer/，推理页可直接选用
                var singerDir = Path.Combine(WorkDir, "data_svc", "singer");
                Directory.CreateDirectory(singerDir);
                var outPath = Path.Combine(singerDir, $"{outName}.spk.npy");
                NpyFile.WriteVector(outPath, mixed);

                _mainWindow.AppendLog($"音色混合完成: {outPath}（{vectors.Count} 个来源，{dim} 维）", LogLevel.SUCCESS);
                _mainWindow.AppendLog("提示: 在推理页将角色目录选为 data_svc/singer 即可使用该音色", LogLevel.INFO);
            }
            catch (Exception ex)
            {
                _mainWindow.AppendLog($"音色混合失败: {ex.Message}", LogLevel.ERROR);
            }
        }

        // ==================== 特征检索索引 ====================

        private void BuildIndex_Click(object sender, RoutedEventArgs e)
        {
            // 预检：索引基于预处理产出的 hubert/whisper 特征
            var hubertDir = Path.Combine(WorkDir, "data_svc", "hubert");
            var whisperDir = Path.Combine(WorkDir, "data_svc", "whisper");
            if (!Directory.Exists(hubertDir) || !Directory.EnumerateFileSystemEntries(hubertDir).Any())
            {
                _mainWindow.AppendLog("未找到 data_svc/hubert 特征，请先完成预处理", LogLevel.ERROR);
                return;
            }
            if (!Directory.Exists(whisperDir) || !Directory.EnumerateFileSystemEntries(whisperDir).Any())
            {
                _mainWindow.AppendLog("未找到 data_svc/whisper 特征，请先完成预处理", LogLevel.ERROR);
                return;
            }

            // 可选：仅对指定说话人构建索引（逗号或空格分隔）
            var args = "";
            var speakerFilter = (IndexSpeakerBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(speakerFilter))
            {
                var speakers = speakerFilter
                    .Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct()
                    .ToList();
                foreach (var spk in speakers)
                {
                    if (!Directory.Exists(Path.Combine(hubertDir, spk)))
                        _mainWindow.AppendLog($"提示: 未在 data_svc/hubert 下找到说话人 {spk}", LogLevel.WARNING);
                }
                args = "--speakers " + string.Join(" ", speakers);
            }

            StartTool("index", "构建检索索引",
                _mainWindow.ResolvedPythonPath, "svc_train_retrieval.py", args);
        }

        // ==================== 训练数据加载测试 ====================

        /// <summary>
        /// 运行 prepare/preprocess_zzz.py：遍历验证集并过一遍训练 DataLoader，
        /// 用于在训练前确认所有特征文件可被正确加载
        /// </summary>
        private void TestDataLoader_Click(object sender, RoutedEventArgs e)
        {
            var validList = Path.Combine(WorkDir, "files", "valid.txt");
            if (!File.Exists(validList))
            {
                _mainWindow.AppendLog("未找到 files/valid.txt，请先完成预处理", LogLevel.ERROR);
                return;
            }

            StartTool("dataloader", "训练数据加载测试",
                _mainWindow.ResolvedPythonPath, "prepare/preprocess_zzz.py", "");
        }

        // ==================== 单音频特征提取测试 ====================

        private async void SelectTestAudio_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".flac");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".ogg");
            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _testAudioPath = file.Path;
                TestAudioNameText.Text = file.Name;
                _mainWindow.AppendLog($"已选择测试音频: {file.Path}", LogLevel.INFO);
            }
        }

        /// <summary>
        /// 对单个音频依次执行 PPG(Whisper-v3) / HuBERT / F0(RMVPE) 提取，
        /// 产出 svc_tmp.ppg.npy / svc_tmp.vec.npy / svc_tmp.pit.csv，
        /// 用于在推理前确认各特征提取环节正常
        /// </summary>
        private void FeatureExtract_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_testAudioPath) || !File.Exists(_testAudioPath))
            {
                _mainWindow.AppendLog("请先选择测试音频文件", LogLevel.WARNING);
                return;
            }

            // 预检三个提取器依赖的预训练模型
            string[] required =
            {
                Path.Combine(WorkDir, "whisper_pretrain", "large-v3.pt"),
                Path.Combine(WorkDir, "hubert_pretrain", "hubert-soft-0d54a1f4.pt"),
                Path.Combine(WorkDir, "pretrain", "rmvpe.pt"),
            };
            foreach (var f in required)
            {
                if (!File.Exists(f))
                {
                    _mainWindow.AppendLog($"缺少预训练模型: {f}", LogLevel.ERROR);
                    return;
                }
            }

            // 与推理页相同的安全路径处理：文件名含空格/特殊字符时先复制到临时目录
            var safeAudioPath = _testAudioPath;
            var fileName = Path.GetFileName(_testAudioPath);
            if (fileName.Contains(' ') || fileName.Contains(';') ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                var safeName = string.Concat(fileName.Select(c =>
                    char.IsWhiteSpace(c) || c == ';' || invalid2.Contains(c) ? '_' : c));
                var tempDir = Path.Combine(Path.GetTempPath(), "SoVITS_FeatureTest");
                Directory.CreateDirectory(tempDir);
                safeAudioPath = Path.Combine(tempDir, safeName);
                try
                {
                    File.Copy(_testAudioPath, safeAudioPath, overwrite: true);
                    _mainWindow.AppendLog($"音频已复制到安全路径: {safeName}", LogLevel.INFO);
                }
                catch (Exception ex)
                {
                    _mainWindow.AppendLog($"复制音频失败: {ex.Message}", LogLevel.ERROR);
                    return;
                }
            }

            // 组合提取命令链；任一步失败即中止（|| exit /b 1）
            var py = _mainWindow.ResolvedPythonPath;
            var commands = new[]
            {
                $"\"{py}\" whisper/inferencev3.py -w \"{safeAudioPath}\" -p svc_tmp.ppg.npy || exit /b 1",
                $"\"{py}\" hubert/inference.py -w \"{safeAudioPath}\" -v svc_tmp.vec.npy || exit /b 1",
                $"\"{py}\" pitch/inference.py -w \"{safeAudioPath}\" -p svc_tmp.pit.csv || exit /b 1",
            };

            var batPath = BuildCondaBatch(commands);
            StartTool("feature-test", "单音频特征提取",
                "cmd.exe", "/c", $"\"{batPath}\"", batPath);
        }

        private static readonly char[] invalid2 = Path.GetInvalidFileNameChars();

        /// <summary>
        /// 生成临时批处理：配置了 Conda 环境时先激活环境再执行命令链。
        /// 三个提取脚本需要顺序执行并在同一进程环境中共享，故用 bat 串联
        /// </summary>
        private string BuildCondaBatch(string[] commands)
        {
            var condaPath = _mainWindow.Settings.CondaEnvPath;

            var lines = new List<string> { "@echo off", "chcp 65001 > nul" };

            if (!string.IsNullOrEmpty(condaPath))
            {
                // 优先用 conda 根目录的 activate.bat；找不到时用环境自带的
                var activateBat = FindCondaActivate(condaPath);
                lines.Add($"call \"{activateBat}\" \"{condaPath}\"");
                // 激活后统一使用环境内的 python
                for (int i = 0; i < commands.Length; i++)
                    commands[i] = commands[i].Replace($"\"{_mainWindow.ResolvedPythonPath}\"", "python");
            }

            lines.Add($"cd /d \"{WorkDir}\"");
            lines.AddRange(commands);

            var batPath = Path.Combine(Path.GetTempPath(), $"sovits_adv_{Guid.NewGuid():N}.bat");
            File.WriteAllText(batPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            return batPath;
        }

        private static string FindCondaActivate(string condaPath)
        {
            var condaExe = Environment.GetEnvironmentVariable("CONDA_EXE");
            if (!string.IsNullOrEmpty(condaExe) && File.Exists(condaExe))
            {
                var root = Path.GetDirectoryName(Path.GetDirectoryName(condaExe));
                var candidate = Path.Combine(root ?? "", "Scripts", "activate.bat");
                if (File.Exists(candidate)) return candidate;
            }

            string[] searchRoots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "anaconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "miniconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3"),
                @"C:\ProgramData\anaconda3",
                @"C:\ProgramData\miniconda3",
            };
            foreach (var root in searchRoots)
            {
                var candidate = Path.Combine(root, "Scripts", "activate.bat");
                if (File.Exists(candidate)) return candidate;
            }

            // 兜底：直接用目标环境自带的 activate
            return Path.Combine(condaPath, "Scripts", "activate.bat");
        }
    }
}
