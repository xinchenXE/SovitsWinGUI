using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace SoVITS_GUI
{
    public partial class App : Application
    {
        private Window? m_window;

        public App()
        {
            this.InitializeComponent();
            // 全局异常兜底：async void 事件处理器中的未捕获异常不再导致应用直接闪退
            this.UnhandledException += OnUnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();
        }

        /// <summary>
        /// 未处理异常兜底：标记已处理避免闪退，并把异常写入本地 crash.log 便于事后排查
        /// </summary>
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoVITS_GUI");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
            }
            catch { /* 兜底逻辑自身出错时忽略 */ }

            try
            {
                if (m_window?.Content != null)
                {
                    var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                    {
                        Title = "发生错误",
                        Content = $"应用遇到未处理的异常，已记录到本地日志：\n{e.Exception?.Message}",
                        CloseButtonText = "确定",
                        XamlRoot = m_window.Content.XamlRoot,
                    };
                    _ = dialog.ShowAsync();
                }
            }
            catch { /* 弹窗失败时忽略 */ }
        }
    }
}
