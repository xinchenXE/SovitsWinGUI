using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SoVITS_GUI.Services;

namespace SoVITS_GUI.Pages
{
    public sealed partial class TrainingPage : Page, ISettingsAware
    {
        private readonly MainWindow _mainWindow;
        private bool _isMyProcess;
        // TensorBoard 独立进程句柄：不复用 PythonProcessService 的单进程槽位，
        // 避免启动 TensorBoard 时把正在跑的训练进程杀掉
        private Process? _tensorboardProcess;
        // 记录上一次加载配置的路径，项目路径变化时才重新读取 YAML
        private string _lastLoadedConfigPath = "";

        public TrainingPage(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            this.InitializeComponent();

            BatchSizeSlider.ValueChanged += BatchSizeSlider_ValueChanged;
            BatchSizeLabel.Text = $"当前：{BatchSizeSlider.Value:F0}（推荐 6G 显存）";
            ModelNameBox.LostFocus += (_, _) => RefreshCheckpointList();

            _mainWindow.ProcessService.ProcessExited += OnProcessExited;

            // 应用退出时一并结束 TensorBoard 进程，避免孤儿进程
            _mainWindow.RegisterExitCleanup(() =>
            {
                try { _tensorboardProcess?.Kill(entireProcessTree: true); } catch { }
            });

            OnSettingsChanged();

            // 恢复上次的模型名
            if (!string.IsNullOrWhiteSpace(_mainWindow.Settings.ModelName))
                ModelNameBox.Text = _mainWindow.Settings.ModelName;
        }

        /// <summary>
        /// 工作目录：优先使用用户配置的项目路径
        /// </summary>
        private string WorkDir => string.IsNullOrEmpty(_mainWindow.Settings.ProjectPath)
            ? AppDomain.CurrentDomain.BaseDirectory
            : _mainWindow.Settings.ProjectPath;

        /// <summary>
        /// 训练配置路径：跟随项目目录，修复此前固定取应用目录导致改错配置的问题
        /// </summary>
        private string ConfigPath => Path.Combine(WorkDir, "configs", "base.yaml");

        private void OnProcessExited(object? sender, int exitCode)
        {
            if (!_isMyProcess) return;
            _isMyProcess = false;
            StartTrainBtn.IsEnabled = true;
            ResumeTrainBtn.IsEnabled = true;
            StopTrainBtn.IsEnabled = false;
            // 训练结束后可能产生新的检查点，刷新恢复列表
            RefreshCheckpointList();
        }

        public void OnSettingsChanged()
        {
            var path = _mainWindow.Settings.ProjectPath ?? "";
            ProjectPathLabel.Text = string.IsNullOrEmpty(path)
                ? "" : path;

            // 仅当配置文件位置变化时才重新加载，避免覆盖用户正在编辑的参数
            var cfg = ConfigPath;
            if (cfg != _lastLoadedConfigPath)
            {
                _lastLoadedConfigPath = cfg;
                LoadConfigValues();
            }
            RefreshCheckpointList();
        }

        public void OnPaneStateChanged(bool isPaneOpen) { }

        private void LoadConfigValues()
        {
            var configPath = ConfigPath;
            if (!File.Exists(configPath)) return;

            try
            {
                var yaml = File.ReadAllText(configPath);

                var lrMatch = Regex.Match(yaml, @"^\s*learning_rate:\s*([^\s#]+)", RegexOptions.Multiline);
                if (lrMatch.Success && double.TryParse(lrMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    LearningRateBox.Text = lrMatch.Groups[1].Value;

                var bsMatch = Regex.Match(yaml, @"^\s*batch_size:\s*(\d+)", RegexOptions.Multiline);
                if (bsMatch.Success && int.TryParse(bsMatch.Groups[1].Value, out var bs) && bs >= 1 && bs <= 16)
                    BatchSizeSlider.Value = bs;

                var logMatch = Regex.Match(yaml, @"^\s*info_interval:\s*(\d+)", RegexOptions.Multiline);
                if (logMatch.Success) LogIntervalBox.Text = logMatch.Groups[1].Value;

                var saveMatch = Regex.Match(yaml, @"^\s*save_interval:\s*(\d+)", RegexOptions.Multiline);
                if (saveMatch.Success) SaveIntervalBox.Text = saveMatch.Groups[1].Value;

                var pretrainMatch = Regex.Match(yaml, @"^\s*pretrain:\s*""([^""]*)""", RegexOptions.Multiline);
                UsePretrainBox.IsChecked = pretrainMatch.Success && !string.IsNullOrWhiteSpace(pretrainMatch.Groups[1].Value);
            }
            catch (Exception ex)
            {
                _mainWindow.AppendLog($"读取配置文件失败: {ex.Message}", LogLevel.WARNING);
            }
        }

        /// <summary>
        /// 把界面参数写回 YAML 配置；参数非法时返回 false 并阻止启动
        /// </summary>
        private bool ApplyConfigValues()
        {
            var configPath = ConfigPath;
            if (!File.Exists(configPath))
            {
                _mainWindow.AppendLog($"配置文件不存在: {configPath}", LogLevel.ERROR);
                return false;
            }

            // 学习率格式校验，避免把非法值写进 YAML 导致训练直接崩溃
            if (!double.TryParse(LearningRateBox.Text?.Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                _mainWindow.AppendLog($"学习率格式无效: {LearningRateBox.Text}（示例: 5e-5）", LogLevel.ERROR);
                return false;
            }
            if (!int.TryParse(LogIntervalBox.Text?.Trim(), out _) ||
                !int.TryParse(SaveIntervalBox.Text?.Trim(), out _))
            {
                _mainWindow.AppendLog("日志间隔 / 保存间隔必须为整数", LogLevel.ERROR);
                return false;
            }

            try
            {
                var yaml = File.ReadAllText(configPath);

                yaml = Regex.Replace(yaml, @"^(\s*learning_rate:\s*)[^\s#]+",
                    $"$1{LearningRateBox.Text.Trim()}", RegexOptions.Multiline);
                yaml = Regex.Replace(yaml, @"^(\s*batch_size:\s*)\d+",
                    $"$1{(int)BatchSizeSlider.Value}", RegexOptions.Multiline);
                yaml = Regex.Replace(yaml, @"^(\s*info_interval:\s*)\d+",
                    $"$1{LogIntervalBox.Text.Trim()}", RegexOptions.Multiline);
                yaml = Regex.Replace(yaml, @"^(\s*save_interval:\s*)\d+",
                    $"$1{SaveIntervalBox.Text.Trim()}", RegexOptions.Multiline);

                // 底模开关：关闭时写空字符串（train.py 用 os.path.isfile 判断，空串即跳过加载）。
                // 不再整体删除 pretrain 行——旧实现重新开启时在文件末尾追加，
                // 会把键错误地挂到 dist_config 节点下
                var pretrainValue = UsePretrainBox.IsChecked == true
                    ? "\"./vits_pretrain/sovits5.0.pretrain.pth\""
                    : "\"\"";
                if (Regex.IsMatch(yaml, @"^\s*pretrain:", RegexOptions.Multiline))
                {
                    yaml = Regex.Replace(yaml, @"^(\s*pretrain:\s*)[^\r\n]*",
                        $"$1{pretrainValue}", RegexOptions.Multiline);
                }
                else if (UsePretrainBox.IsChecked == true)
                {
                    // 键缺失时插回 train 段（port 行之后）
                    yaml = Regex.Replace(yaml, @"^(\s*port:\s*[^\r\n]*)",
                        $"$1\n  pretrain: {pretrainValue}", RegexOptions.Multiline);
                }

                File.WriteAllText(configPath, yaml);
                return true;
            }
            catch (Exception ex)
            {
                _mainWindow.AppendLog($"写入配置文件失败: {ex.Message}", LogLevel.ERROR);
                return false;
            }
        }

        /// <summary>
        /// 训练前置检查：训练清单是否生成（即是否跑过预处理）、底模是否存在
        /// </summary>
        private bool ValidateTrainingData()
        {
            if (!File.Exists(ConfigPath))
            {
                _mainWindow.AppendLog($"未找到配置文件 {ConfigPath}", LogLevel.ERROR);
                return false;
            }
            var trainList = Path.Combine(WorkDir, "files", "train.txt");
            if (!File.Exists(trainList))
            {
                _mainWindow.AppendLog("未找到训练清单 files/train.txt，请先在“预处理”页完成数据预处理", LogLevel.ERROR);
                return false;
            }
            if (UsePretrainBox.IsChecked == true)
            {
                var pretrain = Path.Combine(WorkDir, "vits_pretrain", "sovits5.0.pretrain.pth");
                if (!File.Exists(pretrain))
                    _mainWindow.AppendLog($"提示: 未找到底模 {pretrain}，将从头开始训练", LogLevel.WARNING);
            }
            return true;
        }

        private void BatchSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (BatchSizeLabel != null)
                BatchSizeLabel.Text = $"当前：{e.NewValue:F0}";
        }

        private void StartTrain_Click(object sender, RoutedEventArgs e)
        {
            var modelName = ModelNameBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(modelName))
            {
                _mainWindow.AppendLog("请输入模型名称", LogLevel.ERROR);
                return;
            }
            if (!ValidateTrainingData()) return;
            if (!ApplyConfigValues()) return;

            // 使用绝对配置路径，确保工作目录与配置位置不一致时也能正确加载
            var args = $"-c \"{ConfigPath}\" -n {modelName}";
            _mainWindow.AppendLog($"启动训练: 模型={modelName}, 学习率={LearningRateBox.Text.Trim()}, 批次={(int)BatchSizeSlider.Value}", LogLevel.INFO);
            StartTraining(modelName, args);
        }

        /// <summary>
        /// 启动训练进程（新训练与恢复训练共用）
        /// </summary>
        private void StartTraining(string modelName, string args)
        {
            // 持久化模型名
            _mainWindow.Settings.ModelName = modelName;
            _mainWindow.Settings.Save();

            _mainWindow.UpdateStatus("训练中...");

            _isMyProcess = true;
            StartTrainBtn.IsEnabled = false;
            ResumeTrainBtn.IsEnabled = false;
            StopTrainBtn.IsEnabled = true;

            _ = _mainWindow.ProcessService.StartScriptAsync(
                _mainWindow.ResolvedPythonPath,
                "svc_trainer.py",
                args,
                WorkDir);
        }

        private void StopTrain_Click(object sender, RoutedEventArgs e)
        {
            _isMyProcess = false;
            _mainWindow.ProcessService.StopProcess();
            StartTrainBtn.IsEnabled = true;
            ResumeTrainBtn.IsEnabled = true;
            StopTrainBtn.IsEnabled = false;
            _mainWindow.UpdateStatus("");
        }

        /// <summary>
        /// 刷新检查点列表：chkpt/&lt;模型名&gt;/*.pt
        /// </summary>
        private void RefreshCheckpointList()
        {
            if (CheckpointBox == null) return;
            CheckpointBox.Items.Clear();

            var modelName = ModelNameBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(modelName))
            {
                CheckpointBox.PlaceholderText = "请先输入模型名称";
                return;
            }

            var ckptDir = Path.Combine(WorkDir, "chkpt", modelName);
            if (!Directory.Exists(ckptDir))
            {
                CheckpointBox.PlaceholderText = "未找到检查点";
                return;
            }

            var files = Directory.GetFiles(ckptDir, "*.pt")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderByDescending(n => n)
                .ToList();

            foreach (var f in files) CheckpointBox.Items.Add(f);
            CheckpointBox.PlaceholderText = files.Count > 0 ? "选择检查点" : "未找到检查点";
        }

        private void RefreshCkpt_Click(object sender, RoutedEventArgs e)
        {
            RefreshCheckpointList();
        }

        /// <summary>
        /// 从检查点恢复训练（svc_trainer.py -p）
        /// </summary>
        private void ResumeTrain_Click(object sender, RoutedEventArgs e)
        {
            var modelName = ModelNameBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(modelName))
            {
                _mainWindow.AppendLog("请输入模型名称", LogLevel.ERROR);
                return;
            }
            if (CheckpointBox.SelectedItem is not string ckpt || string.IsNullOrEmpty(ckpt))
            {
                _mainWindow.AppendLog("请先选择要恢复的检查点", LogLevel.WARNING);
                return;
            }
            if (!ValidateTrainingData()) return;
            if (!ApplyConfigValues()) return;

            var args = $"-c \"{ConfigPath}\" -n {modelName} -p \"chkpt/{modelName}/{ckpt}\"";
            _mainWindow.AppendLog($"恢复训练: 模型={modelName}, 检查点={ckpt}", LogLevel.INFO);
            StartTraining(modelName, args);
        }

        /// <summary>
        /// 启动/停止 TensorBoard：作为独立进程运行，不打断正在进行的训练
        /// </summary>
        private async void Tensorboard_Click(object sender, RoutedEventArgs e)
        {
            // 已在运行则点击即停止
            if (_tensorboardProcess != null && !_tensorboardProcess.HasExited)
            {
                try { _tensorboardProcess.Kill(entireProcessTree: true); }
                catch (Exception ex) { _mainWindow.AppendLog($"停止 TensorBoard 失败: {ex.Message}", LogLevel.WARNING); }
                _tensorboardProcess = null;
                TensorboardBtnText.Text = "启动 TensorBoard";
                _mainWindow.AppendLog("TensorBoard 已停止", LogLevel.INFO);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = _mainWindow.ResolvedPythonPath,
                Arguments = "-m tensorboard.main --logdir=logs --port=6006",
                WorkingDirectory = WorkDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            try
            {
                _tensorboardProcess = Process.Start(psi);
                TensorboardBtnText.Text = "停止 TensorBoard";
                _mainWindow.AppendLog("TensorBoard 启动中: http://localhost:6006", LogLevel.INFO);

                // 等待服务就绪后再打开浏览器
                await Task.Delay(2500);
                try
                {
                    Process.Start(new ProcessStartInfo("http://localhost:6006") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _mainWindow.AppendLog($"打开浏览器失败: {ex.Message}", LogLevel.WARNING);
                }
            }
            catch (Exception ex)
            {
                _mainWindow.AppendLog($"启动 TensorBoard 失败: {ex.Message}", LogLevel.ERROR);
            }
        }
    }
}
