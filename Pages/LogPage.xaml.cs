using System;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using SoVITS_GUI.Services;

namespace SoVITS_GUI.Pages
{
    public sealed partial class LogPage : Page, ISettingsAware
    {
        private readonly MainWindow _mainWindow;

        // 日志行数上限：长时间训练可能输出数万行，RichTextBlock 无限增长会拖慢 UI、涨内存
        private const int MaxLogBlocks = 2000;

        public LogPage(MainWindow mainWindow)
        {
            this.InitializeComponent();
            _mainWindow = mainWindow;
        }

        public void OnSettingsChanged() { }
        public void OnPaneStateChanged(bool isPaneOpen) { }

        public void AppendLog(string message, LogLevel level)
        {
            var paragraph = new Paragraph();

            if (_mainWindow.Settings.ShowTimestamp)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = $"[{DateTime.Now:HH:mm:ss}] ",
                    Foreground = (SolidColorBrush)Application.Current.Resources["MidGrayBrush"]
                });
            }

            var levelColor = level switch
            {
                LogLevel.INFO => Windows.UI.Color.FromArgb(255, 93, 173, 226),
                LogLevel.WARNING => Windows.UI.Color.FromArgb(255, 240, 165, 0),
                LogLevel.ERROR => Windows.UI.Color.FromArgb(255, 231, 76, 60),
                LogLevel.SUCCESS => Windows.UI.Color.FromArgb(255, 130, 201, 30),
                _ => Windows.UI.Color.FromArgb(255, 100, 100, 100)
            };

            paragraph.Inlines.Add(new Run
            {
                Text = $"[{level}] ",
                Foreground = new SolidColorBrush(levelColor),
                FontWeight = FontWeights.SemiBold
            });

            paragraph.Inlines.Add(new Run
            {
                Text = message,
                Foreground = (SolidColorBrush)Application.Current.Resources["DarkGrayBrush"]
            });

            LogPanel.Blocks.Add(paragraph);

            // 超过上限时移除最早的一条（导出日志仅包含最近 MaxLogBlocks 条）
            if (LogPanel.Blocks.Count > MaxLogBlocks)
                LogPanel.Blocks.RemoveAt(0);

            if (_mainWindow.Settings.AutoScroll)
            {
                ScrollToEnd();
            }
        }

        public string GetLogText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Whisper-vits-svc-LargeV3 日志导出");
            sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('-', 50));

            foreach (var block in LogPanel.Blocks)
            {
                if (block is Paragraph p)
                {
                    foreach (var inline in p.Inlines)
                    {
                        if (inline is Run run)
                            sb.Append(run.Text);
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        public void ClearLogs()
        {
            LogPanel.Blocks.Clear();
        }

        private void ScrollToEnd()
        {
            var parent = LogPanel.Parent as ScrollViewer;
            if (parent == null)
            {
                var border = LogPanel.Parent as Border;
                parent = border?.Parent as ScrollViewer;
            }
            parent?.ChangeView(null, parent.ScrollableHeight, null);
        }

        private async void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            ClearLogs();
            _mainWindow.AppendLog("日志已清空", LogLevel.INFO);
        }

        private async void SaveLog_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.FileTypeChoices.Add("文本文件", new[] { ".txt" });
            picker.SuggestedFileName = $"SoVITS_Log_{DateTime.Now:yyyyMMdd_HHmmss}";

            Services.WindowHelper.InitializePickerWithWindow(picker, _mainWindow);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                var logContent = GetLogText();
                await Windows.Storage.FileIO.WriteTextAsync(file, logContent);
                _mainWindow.AppendLog("日志导出成功", LogLevel.SUCCESS);
            }
        }
    }
}
