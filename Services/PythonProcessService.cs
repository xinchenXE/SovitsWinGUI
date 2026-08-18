using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace SoVITS_GUI.Services
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        INFO,
        WARNING,
        ERROR,
        SUCCESS
    }

    /// <summary>
    /// 日志消息事件参数
    /// </summary>
    public class LogMessageEventArgs : EventArgs
    {
        public string Message { get; }
        public LogLevel Level { get; }
        public DateTime Timestamp { get; }

        public LogMessageEventArgs(string message, LogLevel level)
        {
            Message = message;
            Level = level;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Python 子进程管理服务
    /// 负责启动、监控和终止外部 Python 脚本，并异步捕获输出
    /// </summary>
    public class PythonProcessService : IDisposable
    {
        private readonly object _sync = new();
        private Process? _currentProcess;
        private readonly DispatcherQueue _dispatcherQueue;
        private bool _disposed;
        // 进程序号：每次启动新进程递增，用于识别旧进程"过期"的 Exited 事件，
        // 防止旧进程退出事件错误重置新进程的界面状态
        private int _generation;

        /// <summary>
        /// 收到日志输出时触发
        /// </summary>
        public event EventHandler<LogMessageEventArgs>? LogReceived;

        /// <summary>
        /// 进程退出时触发（仅当前进程，被新启动替换的旧进程不触发）
        /// </summary>
        public event EventHandler<int>? ProcessExited;

        /// <summary>
        /// 进程是否正在运行
        /// </summary>
        public bool IsRunning
        {
            get { lock (_sync) { return _currentProcess != null && !_currentProcess.HasExited; } }
        }

        public PythonProcessService(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;
        }

        /// <summary>
        /// 启动 Python 脚本
        /// </summary>
        /// <param name="pythonPath">Python 解释器路径，null 则使用系统默认 python</param>
        /// <param name="scriptPath">脚本路径</param>
        /// <param name="arguments">命令行参数</param>
        /// <param name="workingDirectory">工作目录</param>
        public Task StartScriptAsync(string pythonPath, string scriptPath, string arguments, string workingDirectory)
        {
            var tcs = new TaskCompletionSource();

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = string.IsNullOrEmpty(pythonPath) ? "python" : pythonPath,
                    // 以 - 开头的参数是 flag（如 -m），不需要引号包裹
                    Arguments = scriptPath.StartsWith("-")
                        ? $"{scriptPath} {arguments}"
                        : $"\"{scriptPath}\" {arguments}",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

                int generation;
                Process? oldProcess;
                lock (_sync)
                {
                    // 已有进程在运行时先取出并终止，避免新旧进程的 Exited 事件互相干扰
                    oldProcess = _currentProcess;
                    _currentProcess = null;
                    generation = ++_generation;
                }
                if (oldProcess != null)
                {
                    KillAndWait(oldProcess);
                    oldProcess.Dispose();
                }

                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true,
                };

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        DispatchLog(e.Data, LogLevel.INFO);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // stderr 默认 INFO（Python 库常将进度信息输出到 stderr），仅关键字匹配时升级
                        var level = e.Data.Contains("Error") || e.Data.Contains("ERROR") || e.Data.Contains("Traceback")
                            ? LogLevel.ERROR
                            : e.Data.Contains("Warning") || e.Data.Contains("WARNING")
                                ? LogLevel.WARNING
                                : LogLevel.INFO;
                        DispatchLog(e.Data, level);
                    }
                };

                process.Exited += (s, e) => OnExitedInternal(process, generation);

                lock (_sync)
                {
                    // 极端情况：启动完成前又被更新的启动替换，放弃本次启动
                    if (_generation != generation)
                    {
                        process.Dispose();
                        tcs.TrySetCanceled();
                        return tcs.Task;
                    }
                    _currentProcess = process;
                }

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                DispatchLog($"已启动进程: {process.StartInfo.FileName} {process.StartInfo.Arguments}", LogLevel.INFO);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                DispatchLog($"启动进程失败: {ex.Message}", LogLevel.ERROR);
                lock (_sync)
                {
                    _currentProcess?.Dispose();
                    _currentProcess = null;
                }
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        /// <summary>
        /// 进程退出的内部处理：仅当该进程仍是当前进程（未被新启动替换）时通知页面
        /// </summary>
        private void OnExitedInternal(Process process, int generation)
        {
            int exitCode;
            try { exitCode = process.ExitCode; }
            catch { exitCode = -1; }

            bool isCurrent;
            lock (_sync)
            {
                isCurrent = generation == _generation && ReferenceEquals(_currentProcess, process);
                if (isCurrent) _currentProcess = null;
            }
            try { process.Dispose(); } catch { }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!isCurrent) return;
                LogReceived?.Invoke(this, new LogMessageEventArgs(
                    exitCode == 0 ? "进程已正常结束" : $"进程异常退出，退出码: {exitCode}",
                    exitCode == 0 ? LogLevel.SUCCESS : LogLevel.ERROR));
                ProcessExited?.Invoke(this, exitCode);
            });
        }

        /// <summary>
        /// 终止当前运行的进程，并等待进程真正退出
        /// </summary>
        public void StopProcess()
        {
            Process? process;
            lock (_sync) { process = _currentProcess; }
            if (process == null) return;
            DispatchLog("正在终止进程...", LogLevel.WARNING);
            KillAndWait(process);
        }

        /// <summary>
        /// 终止整个进程树并短暂等待退出，避免残留状态
        /// </summary>
        private void KillAndWait(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                DispatchLog($"终止进程失败: {ex.Message}", LogLevel.ERROR);
            }
        }

        /// <summary>
        /// 安全地将日志分发到 UI 线程
        /// </summary>
        private void DispatchLog(string message, LogLevel level)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                LogReceived?.Invoke(this, new LogMessageEventArgs(message, level));
            });
        }

        public void Dispose()
        {
            Process? process;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                process = _currentProcess;
                _currentProcess = null;
                // 序号前进，尚未派发的旧 Exited 事件一律忽略
                _generation++;
            }
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { process.Dispose(); } catch { }
            }
        }
    }
}
