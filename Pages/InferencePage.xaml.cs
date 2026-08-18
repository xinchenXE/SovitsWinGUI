using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SoVITS_GUI.Services;
using Windows.Media.Core;

namespace SoVITS_GUI.Pages
{
    internal readonly struct CondaCommand
    {
        public readonly string Exe, Script, Args, WorkDir;
        public CondaCommand(string exe, string script, string args, string workDir)
        { Exe = exe; Script = script; Args = args; WorkDir = workDir; }
    }

    public sealed partial class InferencePage : Page, ISettingsAware
    {
        private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".flac", ".m4a", ".ogg" };

        private readonly MainWindow _mainWindow;
        private string _audioPath = string.Empty;
        private string _projectPath = string.Empty;
        private string _outputWavPath = string.Empty;
        private bool _isMyProcess;
        // 记录本次推理启动时间，退出后只认“新生成”的输出文件，避免把上一次的结果误报为本次产物
        private DateTime _inferStartTime;
        // 本次通过 conda 启动时生成的临时 bat 路径，进程退出后删除，避免 %TEMP% 累积
        private string _pendingBatPath = "";

        public InferencePage(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            this.InitializeComponent();

            PitchShiftSlider.ValueChanged += PitchShiftSlider_ValueChanged;
            WhisperVersionBox.SelectionChanged += WhisperVersionBox_SelectionChanged;

            _mainWindow.ProcessService.ProcessExited += OnProcessExited;

            OnSettingsChanged();
            RestoreFromSettings();

            // 清理历史遗留的临时 bat 文件（上次异常退出未删除的）
            TempBatCleanup.PurgeStale("sovits_infer_");
        }

        /// <summary>
        /// 从持久化设置恢复：变调、Whisper 版本
        /// </summary>
        private void RestoreFromSettings()
        {
            var s = _mainWindow.Settings;
            var shift = Math.Clamp(s.PitchShift, (int)PitchShiftSlider.Minimum, (int)PitchShiftSlider.Maximum);
            PitchShiftSlider.Value = shift;
            UpdateShiftLabel(shift);
            WhisperVersionBox.SelectedIndex = s.WhisperVersion == "large-v2" ? 1 : 0;
        }

        private void OnProcessExited(object? sender, int exitCode)
        {
            if (!_isMyProcess) return;
            _isMyProcess = false;
            // 删除本次推理生成的临时 bat，避免在 %TEMP% 累积
            TempBatCleanup.TryDelete(_pendingBatPath);
            _pendingBatPath = "";
            StartInferBtn.IsEnabled = true;
            StopInferBtn.IsEnabled = false;

            if (exitCode != 0)
            {
                ResultCard.Visibility = Visibility.Collapsed;
                return;
            }

            // 输出位置兼容两种工程：原版工程把 svc_out.wav 写在工程根目录，
            // 修改版可能写到 <工程目录>/.output/（文件名也可能是自定义的），
            // 且只接受本次推理之后写入的文件
            _outputWavPath = FindLatestOutputWav();

            if (!string.IsNullOrEmpty(_outputWavPath))
            {
                ResultPathText.Text = _outputWavPath;
                ResultCard.Visibility = Visibility.Visible;
                TryLoadResultForPlayback();
                _mainWindow.AppendLog($"输出文件: {_outputWavPath}", LogLevel.SUCCESS);
            }
            else
            {
                ResultCard.Visibility = Visibility.Collapsed;
                _mainWindow.AppendLog("推理结束但未检测到新的输出音频（已检查工程目录与 .output 目录），请查看上方日志确认是否出错", LogLevel.WARNING);
            }
        }

        /// <summary>
        /// 查找本次推理产出的音频：优先 .output 目录，其次工程根目录；
        /// 先找固定文件名 svc_out.wav，找不到时退回“时间窗口内最新的 wav”，
        /// 以兼容自定义输出文件名的修改版工程；排除调试产物 svc_out_pit.wav
        /// </summary>
        private string FindLatestOutputWav()
        {
            var minTime = _inferStartTime.AddSeconds(-5);
            var searchDirs = new[]
            {
                Path.Combine(_projectPath, ".output"),
                _projectPath,
            };

            // 1) 两个位置的固定文件名 svc_out.wav
            foreach (var dir in searchDirs)
            {
                try
                {
                    var exact = new FileInfo(Path.Combine(dir, "svc_out.wav"));
                    if (exact.Exists && exact.LastWriteTime >= minTime)
                        return exact.FullName;
                }
                catch { /* 路径非法等情况直接跳过 */ }
            }

            // 2) 两个目录内本次推理时间窗口内最新生成的其他 wav
            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var newest = Directory.EnumerateFiles(dir, "*.wav")
                        .Select(p => new FileInfo(p))
                        .Where(f => f.LastWriteTime >= minTime)
                        .Where(f => !string.Equals(f.Name, "svc_out_pit.wav", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();
                    if (newest != null)
                        return newest.FullName;
                }
                catch (Exception ex)
                {
                    _mainWindow.AppendLog($"搜索输出目录失败: {ex.Message}", LogLevel.WARNING);
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 结果卡片内直接试听输出音频。
        /// 优先用 StorageFile 加载（对含点号目录、中文路径的兼容性更好），
        /// 失败时退回 file:// URI 方式
        /// </summary>
        private async void TryLoadResultForPlayback()
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_outputWavPath);
                ResultPlayer.Source = MediaSource.CreateFromStorageFile(file);
            }
            catch
            {
                try
                {
                    ResultPlayer.Source = MediaSource.CreateFromUri(new Uri(_outputWavPath));
                }
                catch (Exception ex2)
                {
                    _mainWindow.AppendLog($"加载试听音频失败: {ex2.Message}", LogLevel.WARNING);
                }
            }
        }

        public void OnSettingsChanged()
        {
            _projectPath = _mainWindow.Settings.ProjectPath ?? "";
            ProjectPathBox.Text = string.IsNullOrEmpty(_projectPath) ? "" : _projectPath;

            if (!string.IsNullOrEmpty(_projectPath))
                OnProjectPathChanged();
        }

        public void OnPaneStateChanged(bool isPaneOpen) { }

        private void PitchShiftSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            var val = (int)e.NewValue;
            UpdateShiftLabel(val);
        }

        private void UpdateShiftLabel(int val)
        {
            if (PitchShiftLabel == null) return;
            PitchShiftLabel.Text = val switch
            {
                > 0 => $"当前：+{val} 半音（升调）",
                < 0 => $"当前：{val} 半音（降调）",
                _ => "当前：0 半音（原调）"
            };
        }

        private void WhisperVersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WhisperVersionBox == null) return;
            _mainWindow.Settings.WhisperVersion = WhisperVersionBox.SelectedIndex == 1 ? "large-v2" : "large-v3";
            _mainWindow.Settings.Save();
        }

        private void OnProjectPathChanged()
        {
            var configPath = Path.Combine(_projectPath, "configs", "base.yaml");
            if (File.Exists(configPath))
                ConfigPathBox.Text = configPath;

            if (!string.IsNullOrEmpty(SpeakerDirBox.Text)) return;

            // 角色目录优先级：设置中保存的目录 > data_svc/singer（预处理产出）> configs/singers（内置示例）
            var savedDir = _mainWindow.Settings.SpeakerDir;
            var trainedDir = Path.Combine(_projectPath, "data_svc", "singer");
            var builtinDir = Path.Combine(_projectPath, "configs", "singers");

            string? dir = null;
            if (!string.IsNullOrEmpty(savedDir) && Directory.Exists(savedDir) &&
                Directory.GetFiles(savedDir, "*.npy").Length > 0)
                dir = savedDir;
            else if (Directory.Exists(trainedDir) && Directory.GetFiles(trainedDir, "*.npy").Length > 0)
                dir = trainedDir;
            else if (Directory.Exists(builtinDir))
                dir = builtinDir;

            if (dir != null)
            {
                SpeakerDirBox.Text = dir;
                LoadSpeakerList(dir);
            }
        }

        private async void BrowseSpeakerDir_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                SpeakerDirBox.Text = folder.Path;
                _mainWindow.Settings.SpeakerDir = folder.Path;
                _mainWindow.Settings.Save();
                LoadSpeakerList(folder.Path);
                _mainWindow.AppendLog($"角色目录: {folder.Path}", LogLevel.INFO);
            }
        }

        private void LoadSpeakerList(string singersDir)
        {
            SpeakerBox.Items.Clear();
            if (!Directory.Exists(singersDir))
            {
                SpeakerBox.PlaceholderText = "目录不存在，请重新选择";
                return;
            }

            try
            {
                var npyFiles = Directory.GetFiles(singersDir, "*.npy");
                if (npyFiles.Length == 0)
                {
                    SpeakerBox.PlaceholderText = "未找到 .npy 角色文件";
                    return;
                }

                int restoreIndex = -1;
                var savedSpeaker = _mainWindow.Settings.SpeakerFile;
                for (int i = 0; i < npyFiles.Length; i++)
                {
                    var filePath = npyFiles[i];
                    var name = Path.GetFileNameWithoutExtension(filePath);
                    SpeakerBox.Items.Add(new ComboBoxItem { Content = $"🎭 {name}", Tag = filePath });
                    if (!string.IsNullOrEmpty(savedSpeaker) &&
                        string.Equals(filePath, savedSpeaker, StringComparison.OrdinalIgnoreCase))
                        restoreIndex = i;
                }

                // 恢复上次选中的角色，找不到时退回第一项
                SpeakerBox.SelectedIndex = restoreIndex >= 0 ? restoreIndex : 0;
                _mainWindow.AppendLog($"已加载 {npyFiles.Length} 个角色", LogLevel.INFO);
            }
            catch (Exception ex)
            {
                _mainWindow.AppendLog($"加载角色列表失败: {ex.Message}", LogLevel.WARNING);
            }
        }

        private async void BrowseConfig_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");
            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                ConfigPathBox.Text = file.Path;
                _mainWindow.AppendLog($"已选择配置: {file.Path}", LogLevel.INFO);
            }
        }

        private async void BrowseModel_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".pth");
            picker.FileTypeFilter.Add(".pt");
            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                ModelPathBox.Text = file.Path;
                _mainWindow.AppendLog($"已选择模型: {file.Path}", LogLevel.INFO);
            }
        }

        private bool IsSupportedAudio(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? "";
            return AudioExtensions.Contains(ext);
        }

        private void AudioDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "松开放置音频文件";
        }

        private async void AudioDropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is Windows.Storage.StorageFile file)
                {
                    if (!IsSupportedAudio(file.Name))
                    {
                        _mainWindow.AppendLog($"不支持的音频格式: {file.Name}（支持 wav/mp3/flac/m4a/ogg）", LogLevel.WARNING);
                        return;
                    }
                    _audioPath = file.Path;
                    DropHintText.Text = $"✅ {file.Name}";
                    _mainWindow.AppendLog($"已选择音频: {_audioPath}", LogLevel.INFO);
                }
            }
        }

        private async void SelectAudio_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var ext in AudioExtensions) picker.FileTypeFilter.Add(ext);
            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _audioPath = file.Path;
                DropHintText.Text = $"✅ {file.Name}";
                _mainWindow.AppendLog($"已选择音频: {_audioPath}", LogLevel.INFO);
            }
        }

        private CondaCommand BuildCondaCommand(string pythonScript, string pythonArgs, string workDir, string? preCommands = null)
        {
            var condaPath = _mainWindow.Settings.CondaEnvPath;

            if (string.IsNullOrEmpty(condaPath))
            {
                _pendingBatPath = "";
                return new CondaCommand(_mainWindow.ResolvedPythonPath, pythonScript, pythonArgs, workDir);
            }

            // 查找 conda 根目录 (Scripts\activate.bat 所在)
            string? condaRoot = null;
            string? condaExe = Environment.GetEnvironmentVariable("CONDA_EXE");
            if (!string.IsNullOrEmpty(condaExe) && File.Exists(condaExe))
            {
                condaRoot = Path.GetDirectoryName(Path.GetDirectoryName(condaExe));
            }
            else
            {
                string[] searchRoots = [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "anaconda3"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "miniconda3"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3"),
                    @"C:\ProgramData\anaconda3",
                    @"C:\ProgramData\miniconda3",
                ];
                condaRoot = searchRoots.FirstOrDefault(d => File.Exists(Path.Combine(d, "Scripts", "activate.bat")));
            }

            var batPath = Path.Combine(Path.GetTempPath(), $"sovits_infer_{Guid.NewGuid():N}.bat");
            // 找不到 conda 根目录时退回目标环境自带的 activate.bat；
            // 旧实现这里会拼出 python.exe 再 call，属于无效命令
            var activateBat = condaRoot != null
                ? Path.Combine(condaRoot, "Scripts", "activate.bat")
                : Path.Combine(condaPath, "Scripts", "activate.bat");

            var batContent = $"@echo off\r\n" +
                $"chcp 65001 > nul\r\n" +
                $"call \"{activateBat}\" \"{condaPath}\"\r\n" +
                $"cd /d \"{workDir}\"\r\n";
            if (!string.IsNullOrEmpty(preCommands))
                batContent += preCommands;
            batContent += $"python \"{pythonScript}\" {pythonArgs}\r\n";

            File.WriteAllText(batPath, batContent, new System.Text.UTF8Encoding(false));
            _pendingBatPath = batPath;
            return new CondaCommand("cmd.exe", "/c", $"\"{batPath}\"", workDir);
        }

        /// <summary>
        /// 与 svc_inference.py 一致地推导说话人名称：去掉文件名的全部后缀
        /// （例如 eva.spk.npy -> eva），检索索引目录按该名称组织
        /// </summary>
        private static string GetSpeakerName(string speakerPath)
        {
            var fn = Path.GetFileName(speakerPath);
            var idx = fn.IndexOf('.');
            return idx > 0 ? fn[..idx] : fn;
        }

        private void StartInfer_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_projectPath))
            {
                _mainWindow.AppendLog("请先选择项目路径", LogLevel.WARNING);
                return;
            }
            if (string.IsNullOrEmpty(_audioPath))
            {
                _mainWindow.AppendLog("请先选择输入音频文件", LogLevel.WARNING);
                return;
            }
            if (!File.Exists(_audioPath))
            {
                _mainWindow.AppendLog($"输入音频不存在: {_audioPath}", LogLevel.ERROR);
                return;
            }

            // 配置文件：相对路径按项目目录解析，并校验存在性
            var configRaw = ConfigPathBox.Text?.Trim();
            if (string.IsNullOrEmpty(configRaw)) configRaw = "configs/base.yaml";
            var configPath = Path.IsPathRooted(configRaw) ? configRaw : Path.Combine(_projectPath, configRaw);
            if (!File.Exists(configPath))
            {
                _mainWindow.AppendLog($"配置文件不存在: {configPath}", LogLevel.ERROR);
                return;
            }

            // 模型文件：同样解析并校验（默认值 sovits5.0.pth 位于项目根目录）
            var modelRaw = ModelPathBox.Text?.Trim();
            if (string.IsNullOrEmpty(modelRaw))
            {
                _mainWindow.AppendLog("请指定模型文件路径", LogLevel.WARNING);
                return;
            }
            var modelPath = Path.IsPathRooted(modelRaw) ? modelRaw : Path.Combine(_projectPath, modelRaw);
            if (!File.Exists(modelPath))
            {
                _mainWindow.AppendLog($"模型文件不存在: {modelPath}（训练后请先在高级工具页导出模型）", LogLevel.ERROR);
                return;
            }

            var speakerPath = string.Empty;
            if (SpeakerBox.SelectedItem is ComboBoxItem item && item.Tag is string spkPath)
            {
                speakerPath = spkPath;
            }
            else if (!string.IsNullOrWhiteSpace(SpeakerBox.Text))
            {
                speakerPath = SpeakerBox.Text.Trim();
            }
            else
            {
                _mainWindow.AppendLog("请选择目标角色", LogLevel.WARNING);
                return;
            }
            if (!File.Exists(speakerPath))
            {
                _mainWindow.AppendLog($"角色文件不存在: {speakerPath}", LogLevel.ERROR);
                return;
            }
            var speakerName = GetSpeakerName(speakerPath);

            var shift = (int)PitchShiftSlider.Value;

            // 特征检索需要事先构建索引，缺失时 svc_inference.py 会直接抛错，这里提前拦截
            var retrievalFlag = "";
            if (EnableRetrievalBox.IsChecked == true)
            {
                var indexDir = Path.Combine(_projectPath, "data_svc", "indexes", speakerName);
                var hasIndex = Directory.Exists(indexDir) &&
                    Directory.EnumerateFiles(indexDir, "*.index").Any();
                if (!hasIndex)
                {
                    _mainWindow.AppendLog($"未找到角色 {speakerName} 的检索索引（{indexDir}），请先在高级工具页构建，或取消勾选特征检索", LogLevel.ERROR);
                    return;
                }
                retrievalFlag = " --enable-retrieval";
            }

            // 音频文件名含空格或特殊字符时，复制到临时安全路径
            var safeAudioPath = _audioPath;
            var audioFileName = Path.GetFileName(_audioPath);
            if (audioFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || audioFileName.Contains(' ')
                || audioFileName.Contains(';'))
            {
                var safeName = Regex.Replace(audioFileName, @"[\s;]", "_");
                safeName = Regex.Replace(safeName, @"[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]", "_");
                var tempDir = Path.Combine(Path.GetTempPath(), "SoVITS_Infer");
                Directory.CreateDirectory(tempDir);
                safeAudioPath = Path.Combine(tempDir, safeName);
                try
                {
                    File.Copy(_audioPath, safeAudioPath, overwrite: true);
                    _mainWindow.AppendLog($"音频已复制到安全路径: {safeName}", LogLevel.INFO);
                }
                catch (Exception ex)
                {
                    _mainWindow.AppendLog($"复制音频失败: {ex.Message}", LogLevel.ERROR);
                    return;
                }
            }

            // Whisper 版本：预先运行 whisper 推理，然后传 --ppg 跳过 svc_inference 的自动调用。
            // 注意：本仓库的 whisper/audio.py 为 128-mel 前端，与 80-mel 的 large-v2 原始前端不兼容，
            // 因此不再提供旧的 whisper/inference.py（80mel）选项
            var whisperScript = WhisperVersionBox.SelectedIndex == 1
                ? "whisper/inferencev2.py"
                : "whisper/inferencev3.py";
            var whisperPretrain = WhisperVersionBox.SelectedIndex == 1
                ? Path.Combine(_projectPath, "whisper_pretrain", "large-v2.pt")
                : Path.Combine(_projectPath, "whisper_pretrain", "large-v3.pt");
            if (!File.Exists(whisperPretrain))
            {
                _mainWindow.AppendLog($"缺少 Whisper 预训练模型: {whisperPretrain}", LogLevel.ERROR);
                return;
            }

            // 持久化本次推理的选择
            var s = _mainWindow.Settings;
            s.PitchShift = shift;
            s.SpeakerFile = speakerPath;
            s.InputAudioPath = _audioPath;
            s.Save();

            // PPG 提取失败时立即中止（exit /b 1），不再继续执行推理主流程
            var preCommands = $"python \"{whisperScript}\" -w \"{safeAudioPath}\" -p svc_tmp.ppg.npy || exit /b 1\r\n";
            var scriptArgs = $"--config \"{configPath}\" --model \"{modelPath}\" --spk \"{speakerPath}\" --wave \"{safeAudioPath}\" --shift {shift} --ppg svc_tmp.ppg.npy{retrievalFlag}";

            ResultCard.Visibility = Visibility.Collapsed;
            ResultPlayer.Source = null;
            _outputWavPath = string.Empty;

            _mainWindow.AppendLog($"启动推理: 音频={Path.GetFileName(_audioPath)}, 角色={speakerName}, 变调={shift}, Whisper={whisperScript}", LogLevel.INFO);
            _mainWindow.UpdateStatus("转换中...");

            _isMyProcess = true;
            _inferStartTime = DateTime.Now;
            StartInferBtn.IsEnabled = false;
            StopInferBtn.IsEnabled = true;

            var cmd = BuildCondaCommand("svc_inference.py", scriptArgs, _projectPath, preCommands);
            _ = _mainWindow.ProcessService.StartScriptAsync(cmd.Exe, cmd.Script, cmd.Args, cmd.WorkDir);
        }

        private void StopInfer_Click(object sender, RoutedEventArgs e)
        {
            _isMyProcess = false;
            _mainWindow.ProcessService.StopProcess();
            StartInferBtn.IsEnabled = true;
            StopInferBtn.IsEnabled = false;
            _mainWindow.UpdateStatus("");
        }

        private async void SaveResult_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_outputWavPath) || !File.Exists(_outputWavPath))
            {
                _mainWindow.AppendLog("输出文件不存在", LogLevel.WARNING);
                return;
            }

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.FileTypeChoices.Add("WAV 音频", new[] { ".wav" });
            picker.SuggestedFileName = "svc_out.wav";
            WindowHelper.InitializePickerWithWindow(picker, _mainWindow);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                try
                {
                    File.Copy(_outputWavPath, file.Path, overwrite: true);
                    _mainWindow.AppendLog($"音频已保存到: {file.Path}", LogLevel.SUCCESS);
                }
                catch (Exception ex)
                {
                    _mainWindow.AppendLog($"保存失败: {ex.Message}", LogLevel.ERROR);
                }
            }
        }
    }
}
