using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoVITS_GUI.Services;

namespace SoVITS_GUI.Pages
{
    public sealed partial class HomePage : Page, ISettingsAware
    {
        private readonly MainWindow _mainWindow;
        private string _projectPath = string.Empty;

        public HomePage(MainWindow mainWindow)
        {
            this.InitializeComponent();
            _mainWindow = mainWindow;

            OnSettingsChanged();

            var imgPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "img",
                "1e17595441e50a79b4c31c6bae9e3880_8036775604872242697.png");
            if (File.Exists(imgPath))
            {
                WelcomeImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri(imgPath));
            }
        }

        public void OnSettingsChanged()
        {
            _projectPath = _mainWindow.Settings.ProjectPath ?? "";
            ProjectPathBox.Text = string.IsNullOrEmpty(_projectPath)
                ? "" : _projectPath;
        }

        public void OnPaneStateChanged(bool isPaneOpen) { }

        private void QuickPreprocess_Click(object sender, RoutedEventArgs e) => _mainWindow.NavigateFromExternal("preprocess");

        private void QuickTraining_Click(object sender, RoutedEventArgs e) => _mainWindow.NavigateFromExternal("training");

        private void QuickInference_Click(object sender, RoutedEventArgs e) => _mainWindow.NavigateFromExternal("inference");

        /// <summary>
        /// 检查开发环境（Python、PyTorch 等）
        /// </summary>
        private async void CheckEnv_Click(object sender, RoutedEventArgs e)
        {
            EnvStatusPanel.Children.Clear();
            _mainWindow.AppendLog("开始检查开发环境...", LogLevel.INFO);

            CheckEnvBtn.IsEnabled = false;

            var projectPath = string.IsNullOrEmpty(_projectPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : _projectPath;
            var pythonPath = _mainWindow.ResolvedPythonPath;

            // 检查 Python 版本
            var (pythonOk, pythonVer) = await RunPythonAsync(pythonPath, "--version");
            if (pythonOk)
            {
                var ver = pythonVer.Trim();
                AddEnvStatus($"✅ Python {ver}", true);
                _mainWindow.AppendLog($"Python: {ver}", LogLevel.SUCCESS);
            }
            else
            {
                AddEnvStatus("❌ Python 未安装或无法调用", false);
                _mainWindow.AppendLog($"Python 检测失败: {pythonVer}", LogLevel.ERROR);
                CheckEnvBtn.IsEnabled = true;
                return;
            }

            // 检查 PyTorch 版本与 CUDA 可用性
            // 脚本写入临时 .py 文件执行：旧实现的 -c "..." 参数含内层双引号，
            // 会被 Windows 命令行解析破坏，导致该项检测永远失败
            var torchScript = string.Join("\n",
                "import torch",
                "print(torch.__version__)",
                "print('CUDA: ' + str(torch.cuda.is_available()))",
                "if torch.cuda.is_available():",
                "    print('GPU: ' + torch.cuda.get_device_name(0))",
                "else:",
                "    print('GPU: N/A')");
            var (torchOk, torchOut) = await RunPythonScriptAsync(pythonPath, torchScript);
            if (torchOk)
            {
                var lines = torchOut.Trim().Split('\n');
                var torchVer = lines.Length > 0 ? lines[0].Trim() : "未知";
                var cudaAvail = lines.Length > 1 && lines[1].Trim().Contains("True");
                var gpuName = lines.Length > 2 ? lines[2].Trim() : "";

                AddEnvStatus($"✅ PyTorch {torchVer}", true);
                _mainWindow.AppendLog($"PyTorch: {torchVer}", LogLevel.SUCCESS);

                if (cudaAvail)
                {
                    AddEnvStatus($"✅ CUDA 可用 — {gpuName}", true);
                    _mainWindow.AppendLog($"CUDA 可用 — GPU: {gpuName}", LogLevel.SUCCESS);
                }
                else
                {
                    AddEnvStatus("⚠️ CUDA 不可用，将使用 CPU 推理（速度较慢）", false);
                    _mainWindow.AppendLog("CUDA 不可用", LogLevel.WARNING);
                }
            }
            else
            {
                AddEnvStatus("❌ PyTorch 未安装", false);
                _mainWindow.AppendLog("PyTorch 检测失败", LogLevel.ERROR);
            }

            // 检查关键 Python 包（同样走临时脚本文件；对缺少 __version__ 属性的包做容错）
            var pkgsScript = string.Join("\n",
                "import importlib",
                "import importlib.util",
                "pkgs = ['numpy', 'librosa', 'soundfile', 'scipy', 'omegaconf', 'faiss']",
                "for p in pkgs:",
                "    if importlib.util.find_spec(p) is None:",
                "        print(f'{p}: MISSING')",
                "        continue",
                "    try:",
                "        print(f'{p}: {importlib.import_module(p).__version__}')",
                "    except Exception:",
                "        print(f'{p}: OK')");
            var (pkgsOk, pkgsOut) = await RunPythonScriptAsync(pythonPath, pkgsScript);
            if (pkgsOk && !string.IsNullOrWhiteSpace(pkgsOut))
            {
                foreach (var line in pkgsOut.Trim().Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    var ok = !trimmed.Contains("MISSING");
                    AddEnvStatus(ok ? $"✅ {trimmed}" : $"❌ {trimmed.Replace(": MISSING", " 未安装")}", ok);
                }
            }
            else
            {
                AddEnvStatus("⚠️ 无法检测依赖包", false);
            }

            // 检查项目文件
            var svcInference = Path.Combine(_projectPath, "svc_inference.py");
            var configYaml = Path.Combine(_projectPath, "configs", "base.yaml");
            var hasSvc = File.Exists(svcInference);
            var hasConfig = File.Exists(configYaml);
            AddEnvStatus(hasSvc ? "✅ 检测到 svc_inference.py" : "⚠️ 未找到 svc_inference.py", hasSvc);
            AddEnvStatus(hasConfig ? "✅ 检测到 configs/base.yaml" : "⚠️ 未找到 configs/base.yaml", hasConfig);

            _mainWindow.AppendLog("环境检查完成", LogLevel.SUCCESS);
            CheckEnvBtn.IsEnabled = true;
        }

        private void AddEnvStatus(string text, bool ok)
        {
            EnvStatusPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    ok ? Windows.UI.Color.FromArgb(255, 130, 201, 30)
                       : Windows.UI.Color.FromArgb(255, 231, 76, 60))
            });
        }

        /// <summary>
        /// 执行 Python 命令；stdout/stderr 并发读取，避免顺序读取时的管道死锁
        /// </summary>
        private async System.Threading.Tasks.Task<(bool success, string output)> RunPythonAsync(string pythonPath, string args)
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8,
                    },
                };
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var outputs = await System.Threading.Tasks.Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync();
                if (process.ExitCode == 0)
                    return (true, outputs[0]);
                return (false, outputs[1].Length > 0 ? outputs[1] : outputs[0]);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// 把脚本内容写入临时 .py 文件后执行，彻底规避 -c 参数的引号解析问题；执行完删除临时文件
        /// </summary>
        private async System.Threading.Tasks.Task<(bool success, string output)> RunPythonScriptAsync(string pythonPath, string scriptContent)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "SoVITS_EnvCheck");
            Directory.CreateDirectory(tempDir);
            var scriptPath = Path.Combine(tempDir, $"envcheck_{Guid.NewGuid():N}.py");
            try
            {
                File.WriteAllText(scriptPath, scriptContent, new System.Text.UTF8Encoding(false));
                return await RunPythonAsync(pythonPath, $"\"{scriptPath}\"");
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { /* 清理失败可忽略 */ }
            }
        }
    }
}
