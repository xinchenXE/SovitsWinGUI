using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using SoVITS_GUI.Models;
using SoVITS_GUI.Pages;
using SoVITS_GUI.Services;

namespace SoVITS_GUI
{
    public sealed partial class MainWindow : Window
    {
        private readonly Dictionary<string, Page> _pages = new();
        private readonly PythonProcessService _processService;
        private readonly SystemMonitorService _monitorService;
        private readonly DispatcherQueue _dispatcherQueue;
        private bool _isPaneOpen = false;

        public bool IsPaneOpen => _isPaneOpen;
        public PythonProcessService ProcessService => _processService;
        public AppConfig Settings { get; }

        public string ResolvedPythonPath
        {
            get
            {
                // 手动指定路径优先
                var envPath = !string.IsNullOrEmpty(Settings.ManualCondaPath)
                    ? Settings.ManualCondaPath
                    : Settings.CondaEnvPath;

                if (!string.IsNullOrEmpty(envPath))
                {
                    var pythonExe = Path.Combine(envPath, "python.exe");
                    if (File.Exists(pythonExe)) return pythonExe;
                }
                return "python";
            }
        }

        public MainWindow()
        {
            this.InitializeComponent();
            this.SystemBackdrop = new MicaBackdrop();

            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "img", "app_icon.ico");
            if (File.Exists(iconPath))
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                appWindow.SetIcon(iconPath);
            }

            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _processService = new PythonProcessService(_dispatcherQueue);
            _monitorService = new SystemMonitorService();
            Settings = AppConfig.Load();

            _processService.LogReceived += OnLogReceived;
            _processService.ProcessExited += OnProcessExited;

            _monitorService.StatsUpdated += OnStatsUpdated;
            _monitorService.Start(_dispatcherQueue);

            _pages["home"] = new HomePage(this);
            _pages["preprocess"] = new PreprocessPage(this);
            _pages["training"] = new TrainingPage(this);
            _pages["inference"] = new InferencePage(this);
            _pages["advanced"] = new AdvancedPage(this);
            _pages["settings"] = new SettingsPage(this);
            _pages["log"] = new LogPage(this);

            NavView.SelectedItem = NavView.MenuItems[0];
            NavigateTo("home");

            // 启动时检测系统亮暗模式并同步
            _isDark = IsSystemDarkMode();
            if (_isDark)
            {
                DarkModeIcon.Glyph = "";
                if (this.Content is FrameworkElement fe)
                    fe.RequestedTheme = ElementTheme.Dark;
            }

            // 启动时从系统配色同步颜色
            InitAccentColors();
            ApplyTheme();

            // 监听 Windows 系统主题色变化，动态更新配色
            _uiSettings.ColorValuesChanged += (s, _) =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    InitAccentColors();
                    ApplyTheme();
                });
            };

            // 监听 IsPaneOpen 属性变化（立即响应，无动画延迟）
            NavView.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, OnIsPaneOpenPropertyChanged);

            ContentFrame.Loaded += (_, _) =>
            {
                _isPaneOpen = NavView.IsPaneOpen;
                ApplyPaneState(_isPaneOpen);
                if (ContentFrame.Content is ISettingsAware page) page.OnPaneStateChanged(_isPaneOpen);
            };

            // 窗口关闭时清理子进程与资源，避免训练/TensorBoard 成为孤儿进程
            this.Closed += OnWindowClosed;
        }

        private readonly List<Action> _exitCleanups = new();

        /// <summary>
        /// 注册应用退出时需要执行的清理动作（供页面清理各自持有的独立子进程）
        /// </summary>
        public void RegisterExitCleanup(Action cleanup) => _exitCleanups.Add(cleanup);

        /// <summary>
        /// 窗口关闭事件：终止 Python 子进程、执行页面注册的清理动作、释放监控服务
        /// </summary>
        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            try { _processService.Dispose(); } catch { }
            foreach (var cleanup in _exitCleanups)
            {
                try { cleanup(); } catch { }
            }
            try { _monitorService.Dispose(); } catch { }
        }

        private bool _isNavigating = false;

        public async void NavigateTo(string tag)
        {
            if (_isNavigating) return;
            if (!_pages.TryGetValue(tag, out var newPage)) return;

            if (newPage is ISettingsAware sa) sa.OnSettingsChanged();

            var oldContent = ContentFrame.Content as UIElement;
            if (oldContent == newPage)
            {
                ContentFrame.Content = newPage;
                return;
            }

            _isNavigating = true;

            try
            {

            if (oldContent != null)
            {
                var oldVisual = ElementCompositionPreview.GetElementVisual(oldContent);
                var oldCompositor = oldVisual.Compositor;
                var slideOut = oldCompositor.CreateScalarKeyFrameAnimation();
                slideOut.InsertKeyFrame(0f, 0f);
                slideOut.InsertKeyFrame(1f, -30f);
                slideOut.Duration = TimeSpan.FromMilliseconds(180);

                var fadeOut = oldCompositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(150);

                oldVisual.StartAnimation("Offset.Y", slideOut);
                oldVisual.StartAnimation("Opacity", fadeOut);
                await Task.Delay(160);
            }

            ContentFrame.Content = newPage;

            if (oldContent != null)
            {
                var newVisual = ElementCompositionPreview.GetElementVisual((UIElement)newPage);
                newVisual.Offset = new Vector3(0, 30, 0);
                newVisual.Opacity = 0;

                var compositor = newVisual.Compositor;
                var slideIn = compositor.CreateScalarKeyFrameAnimation();
                slideIn.InsertKeyFrame(0f, 30f);
                slideIn.InsertKeyFrame(1f, 0f);
                slideIn.Duration = TimeSpan.FromMilliseconds(220);

                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromMilliseconds(200);

                newVisual.StartAnimation("Offset.Y", slideIn);
                newVisual.StartAnimation("Opacity", fadeIn);

                await Task.Delay(230);
            }

            }
            finally
            {
                _isNavigating = false;
            }
        }

        /// <summary>
        /// 供页面内部快捷入口调用：同步导航栏选中项并跳转
        /// </summary>
        public void NavigateFromExternal(string tag)
        {
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem nvi && nvi.Tag is string t && t == tag)
                {
                    NavView.SelectedItem = nvi;
                    break;
                }
            }
            NavigateTo(tag);
        }

        private LogPage? LogPage => _pages.TryGetValue("log", out var p) ? p as LogPage : null;

        /// <summary>
        /// 向日志面板添加一条彩色日志
        /// </summary>
        public void AppendLog(string message, LogLevel level)
        {
            _dispatcherQueue.TryEnqueue(() => LogPage?.AppendLog(message, level));
        }

        public string GetLogText()
        {
            return LogPage?.GetLogText() ?? "";
        }

        /// <summary>
        /// 清空日志面板
        /// </summary>
        public void ClearLogs()
        {
            LogPage?.ClearLogs();
        }

        /// <summary>
        /// 更新状态文字（保留接口兼容，当前底部栏已改为主题切换）
        /// </summary>
        public void UpdateStatus(string status) { }

        /// <summary>
        /// IsPaneOpen 属性变化时立即响应（比 PaneOpened/PaneClosed 更早触发）
        /// </summary>
        private void OnIsPaneOpenPropertyChanged(DependencyObject sender, DependencyProperty dp)
        {
            _isPaneOpen = NavView.IsPaneOpen;
            _dispatcherQueue.TryEnqueue(() => ApplyPaneState(_isPaneOpen));
        }

        private void ApplyPaneState(bool isOpen)
        {
            var vis = isOpen ? Visibility.Visible : Visibility.Collapsed;
            DarkModeLabel.Visibility = vis;
            DarkModeBtn.Width = isOpen ? double.NaN : 48;
        }

        /// <summary>
        /// 导航栏展开时：通知页面
        /// </summary>
        private void NavView_PaneOpened(NavigationView sender, object args)
        {
            if (ContentFrame.Content is ISettingsAware page) page.OnPaneStateChanged(true);
        }

        /// <summary>
        /// 导航栏折叠时：通知页面
        /// </summary>
        private void NavView_PaneClosed(NavigationView sender, object args)
        {
            if (ContentFrame.Content is ISettingsAware page) page.OnPaneStateChanged(false);
        }

        private bool _isDark;
        private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

        // 系统主题色
        private Windows.UI.Color _accent;
        // 亮色主题色值
        private Windows.UI.Color L_Primary, L_Soft, L_Light, L_Teal;
        private readonly Windows.UI.Color L_DarkText  = Windows.UI.Color.FromArgb(255, 0x1a, 0x1a, 0x1a);
        private readonly Windows.UI.Color L_MidText   = Windows.UI.Color.FromArgb(255, 0x66, 0x66, 0x66);
        private readonly Windows.UI.Color L_LightGray = Windows.UI.Color.FromArgb(255, 0xcc, 0xcc, 0xcc);
        private readonly Windows.UI.Color L_CardBg    = Windows.UI.Color.FromArgb(255, 0xF5, 0xF5, 0xF5);
        private readonly Windows.UI.Color L_CardBorder= Windows.UI.Color.FromArgb(255, 0xE0, 0xE0, 0xE0);
        private readonly Windows.UI.Color L_SecondBtn = Windows.UI.Color.FromArgb(255, 0xF0, 0xF0, 0xF0);
        // 暗色主题色值
        private Windows.UI.Color D_Primary, D_Soft, D_Light, D_Teal;
        private readonly Windows.UI.Color D_DarkText  = Windows.UI.Color.FromArgb(255, 0xE0, 0xE0, 0xE0);
        private readonly Windows.UI.Color D_MidText   = Windows.UI.Color.FromArgb(255, 0x99, 0x99, 0x99);
        private readonly Windows.UI.Color D_LightGray = Windows.UI.Color.FromArgb(255, 0x55, 0x55, 0x55);
        private readonly Windows.UI.Color D_CardBg    = Windows.UI.Color.FromArgb(255, 0x1E, 0x1E, 0x1E);
        private readonly Windows.UI.Color D_CardBorder= Windows.UI.Color.FromArgb(255, 0x38, 0x38, 0x38);
        private readonly Windows.UI.Color D_SecondBtn = Windows.UI.Color.FromArgb(255, 0x2A, 0x2A, 0x2A);

        private void InitAccentColors()
        {
            try
            {
                _accent = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            }
            catch
            {
                _accent = Windows.UI.Color.FromArgb(255, 0x3d, 0xb8, 0xa0);
            }

            L_Primary = _accent;
            L_Soft    = LerpColor(_accent, 0.5f);
            L_Light   = LerpColor(_accent, 0.85f);
            L_Teal    = LerpColor(_accent, 0.2f);
            D_Primary = LerpColor(_accent, 0.2f);
            D_Soft    = LerpColor(_accent, -0.3f);
            D_Light   = LerpColor(_accent, -0.5f);
            D_Teal    = LerpColor(_accent, -0.1f);
        }

        private static Windows.UI.Color LerpColor(Windows.UI.Color c, float amount)
        {
            byte lerp(byte v) => (byte)Math.Clamp((int)(v + amount * 255), 0, 255);
            return Windows.UI.Color.FromArgb(255, lerp(c.R), lerp(c.G), lerp(c.B));
        }

        private static readonly string[] SolidBrushKeys =
        {
            "MintGreenBrush", "SoftMintBrush", "LightMintBrush", "TealBlueBrush",
            "DarkGrayBrush", "MidGrayBrush", "LightGrayBrush",
            "TextPrimaryBrush", "TextSecondaryBrush",
            "CardBackgroundBrush", "CardBorderBrush", "SecondaryButtonBgBrush"
        };
        private static readonly string[] AcrylicBrushKeys = { "LogAcrylicBrush", "CardAcrylicBrush" };

        /// <summary>
        /// 通过系统背景色亮度判断当前是否为暗色模式
        /// </summary>
        private bool IsSystemDarkMode()
        {
            try
            {
                var bg = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                // 感知亮度公式 (0.299R + 0.587G + 0.114B)，小于 128 判定为暗色
                return (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) < 128;
            }
            catch
            {
                return false;
            }
        }

        private void DarkMode_Click(object sender, RoutedEventArgs e)
        {
            _isDark = !_isDark;
            DarkModeIcon.Glyph = _isDark ? "" : "";
            if (this.Content is FrameworkElement root)
                root.RequestedTheme = _isDark ? ElementTheme.Dark : ElementTheme.Light;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            var accent  = _isDark ? D_Primary    : L_Primary;
            var soft    = _isDark ? D_Soft       : L_Soft;
            var light   = _isDark ? D_Light      : L_Light;
            var teal    = _isDark ? D_Teal       : L_Teal;
            var text1   = _isDark ? D_DarkText   : L_DarkText;
            var text2   = _isDark ? D_MidText    : L_MidText;
            var gray    = _isDark ? D_LightGray  : L_LightGray;
            var cardBg  = _isDark ? D_CardBg     : L_CardBg;
            var cardBor = _isDark ? D_CardBorder : L_CardBorder;
            var btnBg   = _isDark ? D_SecondBtn  : L_SecondBtn;

            var res = Application.Current.Resources;
            // 实心画刷
            SetSolidBrush(res, SolidBrushKeys[0],  accent);
            SetSolidBrush(res, SolidBrushKeys[1],  soft);
            SetSolidBrush(res, SolidBrushKeys[2],  light);
            SetSolidBrush(res, SolidBrushKeys[3],  teal);
            SetSolidBrush(res, SolidBrushKeys[4],  text1);
            SetSolidBrush(res, SolidBrushKeys[5],  text2);
            SetSolidBrush(res, SolidBrushKeys[6],  gray);
            SetSolidBrush(res, SolidBrushKeys[7],  text1);
            SetSolidBrush(res, SolidBrushKeys[8],  text2);
            SetSolidBrush(res, SolidBrushKeys[9],  cardBg);
            SetSolidBrush(res, SolidBrushKeys[10], cardBor);
            SetSolidBrush(res, SolidBrushKeys[11], btnBg);
            // 亚克力画刷 — 云母/毛玻璃效果，TintColor 比背景略浅
            var acrylicTint = _isDark
                ? Windows.UI.Color.FromArgb(255, 0x28, 0x28, 0x28)
                : Windows.UI.Color.FromArgb(255, 0xF8, 0xF8, 0xF8);
            SetAcrylicBrush(res, AcrylicBrushKeys[0], acrylicTint, cardBg);
            SetAcrylicBrush(res, AcrylicBrushKeys[1], acrylicTint, cardBg);
        }

        private static void SetSolidBrush(ResourceDictionary res, string key, Windows.UI.Color c)
        {
            if (res[key] is SolidColorBrush brush)
                brush.Color = c;
        }

        private static void SetAcrylicBrush(ResourceDictionary res, string key, Windows.UI.Color tint, Windows.UI.Color fallback)
        {
            if (res[key] is AcrylicBrush brush)
            {
                brush.TintColor = tint;
                brush.FallbackColor = fallback;
                brush.TintOpacity = 0.8;
            }
        }


        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                NavigateTo(tag);
            }
        }

        private void OnLogReceived(object? sender, LogMessageEventArgs e)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                AppendLog(e.Message, e.Level);
            });
        }

        private void SyncDiskPanels(List<Services.DiskInfo> disks)
        {
            // 确保子元素数量匹配盘符数量（与 CPU/内存组同构：标题行 + 进度条 + 容量说明）
            while (DiskPanel.Children.Count < disks.Count)
            {
                var group = new StackPanel { Width = 168, Spacing = 4 };
                var header = new Grid();
                var label = new TextBlock { FontSize = 11, Foreground = (SolidColorBrush)Application.Current.Resources["TextSecondaryBrush"] };
                var pctText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Foreground = (SolidColorBrush)Application.Current.Resources["MintGreenBrush"] };
                header.Children.Add(label);
                header.Children.Add(pctText);
                var bar = new ProgressBar { Maximum = 100, Style = (Style)Application.Current.Resources["KawaiiProgressBar"] };
                var sizeText = new TextBlock { FontSize = 10, Foreground = (SolidColorBrush)Application.Current.Resources["TextSecondaryBrush"], TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis };

                group.Children.Add(header);
                group.Children.Add(bar);
                group.Children.Add(sizeText);
                DiskPanel.Children.Add(group);
            }
            while (DiskPanel.Children.Count > disks.Count)
            {
                DiskPanel.Children.RemoveAt(DiskPanel.Children.Count - 1);
            }

            for (int i = 0; i < disks.Count; i++)
            {
                var d = disks[i];
                var group = (StackPanel)DiskPanel.Children[i];
                var header = (Grid)group.Children[0];
                var label = (TextBlock)header.Children[0];
                var pctText = (TextBlock)header.Children[1];
                var bar = (ProgressBar)group.Children[1];
                var sizeText = (TextBlock)group.Children[2];

                label.Text = d.Name;
                bar.Value = d.Percent;
                pctText.Text = $"{d.Percent:F0}%";
                sizeText.Text = $"{d.UsedGB}/{d.TotalGB}G";
            }
        }

        private void OnProcessExited(object? sender, int exitCode) { }

        /// <summary>
        /// 系统状态更新 — 刷新底部进度条和百分比标签
        /// </summary>
        private void OnStatsUpdated(object? sender, SystemStats stats)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                // CPU
                CpuBar.Value = stats.CpuPercent;
                CpuText.Text = $"{stats.CpuPercent:F0}%";
                if (!string.IsNullOrEmpty(stats.CpuName))
                    CpuModelText.Text = stats.CpuName;

                // GPU — 仅显示显存占用
                if (stats.HasGpu)
                {
                    GpuModelText.Text = stats.GpuName;

                    GpuBar.Value = stats.GpuMemoryPercent;
                    GpuPercentText.Text = $"{stats.GpuMemoryPercent:F0}%";
                    GpuVramText.Text = $"{stats.GpuUsedMemoryMB / 1024.0:F1} / {stats.GpuMemoryMB / 1024.0:F1} G";
                }
                else
                {
                    GpuModelText.Text = "未检测到";
                    GpuBar.Value = 0;
                    GpuPercentText.Text = "0%";
                    GpuVramText.Text = "";
                }

                // 内存
                MemBar.Value = stats.MemoryPercent;
                MemText.Text = $"{stats.MemoryPercent:F0}%";
                if (stats.TotalMemoryMB > 0)
                    MemModelText.Text = $"{stats.TotalMemoryMB / 1024.0:F0} G";
                else
                    MemModelText.Text = "";

                // 硬盘 — 动态生成每盘符独立进度条
                SyncDiskPanels(stats.Disks);
            });
        }
    }
}
