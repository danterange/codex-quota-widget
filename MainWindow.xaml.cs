using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Models;
using CodexQuotaWidget.Services;
using CodexQuotaWidget.ViewModels;

namespace CodexQuotaWidget;

/// <summary>
/// 承载额度仪表和设置页的唯一主界面；关闭按钮默认隐藏窗口到托盘，退出只能通过托盘明确执行。
/// </summary>
public partial class MainWindow : Window
{
    private readonly QuotaViewModel viewModel;
    private readonly Action<WidgetSettings> saveSettings;
    private readonly CodexDesktopRestartService restartService = new();
    private readonly CancellationTokenSource restartLifetime = new();
    private WidgetSettings settings;
    private bool applyingControls;
    private bool exitRequested;
    private readonly bool exitOnClose;
    private readonly string? captureScreenshotPath;
    private readonly bool startOnSettings;
    private bool autoSizingLocked;
    private bool userResized;
    private bool suppressUserResizeTracking;
    private bool backdropAvailable;
    private bool restartInProgress;
    private bool forceRestartArmed;
    private int captureAttempts;
    private DispatcherTimer? captureRetryTimer;
    private DispatcherTimer? autoSizingFallbackTimer;
    private DispatcherTimer? restartNoticeTimer;

    /// <summary>初始化数据绑定和设置控件，避免首次显示时触发无意义的保存事件，并识别发布验收的正常退出参数。</summary>
    internal MainWindow(WidgetSettings initialSettings, Action<WidgetSettings> saveSettingsCallback)
    {
        InitializeComponent();
        settings = initialSettings;
        saveSettings = saveSettingsCallback;
        WidgetThemePalette.Apply(settings.Theme);
        exitOnClose = Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--exit-on-close", StringComparison.OrdinalIgnoreCase));
        captureScreenshotPath = Environment.GetCommandLineArgs()
            .Where(argument => argument.StartsWith("--capture-screenshot=", StringComparison.OrdinalIgnoreCase))
            .Select(argument => argument["--capture-screenshot=".Length..].Trim().Trim('"'))
            .FirstOrDefault(path => path.Length > 0);
        startOnSettings = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--start-settings", StringComparison.OrdinalIgnoreCase));
        viewModel = new QuotaViewModel(settings: settings);
        viewModel.PropertyChanged += ViewModelPropertyChanged;
        DataContext = viewModel;
        ApplySettingsToControls();
    }

    /// <summary>允许应用退出流程绕过隐藏逻辑，使 ViewModel 和托盘资源可以被完整释放。</summary>
    public void RequestApplicationExit()
    {
        exitRequested = true;
        Close();
    }

    /// <summary>响应主界面的手动刷新按钮，复用 ViewModel 的串行读取和错误处理。</summary>
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        await viewModel.RefreshNowAsync();
    }

    /// <summary>从小组件发起桌面端专用重启；首次只请求正常关闭，超时后必须由第二次点击和对话框明确确认才允许精确强制结束。</summary>
    private async void RestartCodexClicked(object sender, RoutedEventArgs e)
    {
        if (restartInProgress)
        {
            return;
        }

        var forceAfterTimeout = forceRestartArmed;
        if (forceAfterTimeout && !ConfirmForceRestart())
        {
            return;
        }

        forceRestartArmed = false;
        restartInProgress = true;
        RestartCodexButton.IsEnabled = false;
        RestartCodexButton.ToolTip = viewModel.Text.RestartCodexPreparing;
        SettingsStatusTextBlock.Text = viewModel.Text.RestartCodexPreparing;
        SettingsStatusTextBlock.ToolTip = viewModel.Text.RestartCodexPreparing;
        ShowRestartNotice(viewModel.Text.RestartCodexPreparing, "AccentBrush");
        try
        {
            var result = await restartService.RestartAsync(restartLifetime.Token, forceAfterTimeout);
            forceRestartArmed = result == CodexDesktopRestartResult.NeedsManualClose;
            var message = GetRestartResultMessage(result);
            SettingsStatusTextBlock.Text = message;
            SettingsStatusTextBlock.ToolTip = message;
            var icon = result is CodexDesktopRestartResult.Relaunched or CodexDesktopRestartResult.LoginRequired
                ? System.Windows.Forms.ToolTipIcon.Info
                : System.Windows.Forms.ToolTipIcon.Warning;
            ShowRestartNotice(message, icon == System.Windows.Forms.ToolTipIcon.Info ? "SuccessBrush" : "QuotaRedBrush", TimeSpan.FromSeconds(7));
            (System.Windows.Application.Current as App)?.ShowNotification(viewModel.Text.RestartCodex, message, icon);
        }
        finally
        {
            restartInProgress = false;
            RestartCodexButton.IsEnabled = !restartLifetime.IsCancellationRequested;
            UpdateRestartButtonPresentation();
        }
    }

    /// <summary>在正常关闭超时后要求用户再次确认精确强制关闭，避免一次普通图标点击意外丢弃未保存的 Desktop 内容。</summary>
    private bool ConfirmForceRestart()
    {
        return System.Windows.MessageBox.Show(
                   this,
                   viewModel.Text.RestartCodexForceConfirmation,
                   viewModel.Text.RestartCodexForceConfirmationTitle,
                   System.Windows.MessageBoxButton.YesNo,
                   System.Windows.MessageBoxImage.Warning,
                   System.Windows.MessageBoxResult.No)
               == System.Windows.MessageBoxResult.Yes;
    }

    /// <summary>根据是否已获得强制关闭资格更新重启按钮说明，使下一次点击的副作用在鼠标悬停和辅助技术中可见。</summary>
    private void UpdateRestartButtonPresentation()
    {
        if (RestartCodexButton is null || viewModel is null)
        {
            return;
        }

        var tooltip = forceRestartArmed ? viewModel.Text.RestartCodexNeedsManualClose : viewModel.Text.RestartCodexTooltip;
        RestartCodexButton.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetHelpText(RestartCodexButton, tooltip);
    }

    /// <summary>在公共状态行显示重启进度或结果，使用户即使停留在额度页也能看到安全操作正在发生。</summary>
    private void ShowRestartNotice(string message, string foregroundResourceKey, TimeSpan? clearAfter = null)
    {
        StopRestartNoticeTimer();
        ConnectionStatusPanel.Visibility = Visibility.Collapsed;
        RestartNoticeTextBlock.Text = message;
        RestartNoticeTextBlock.ToolTip = message;
        RestartNoticeTextBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
        RestartNoticeTextBlock.Visibility = Visibility.Visible;
        if (clearAfter is { } delay)
        {
            restartNoticeTimer = new DispatcherTimer { Interval = delay };
            restartNoticeTimer.Tick += RestartNoticeTimerTick;
            restartNoticeTimer.Start();
        }
    }

    /// <summary>在短暂结果提示结束后恢复实时连接状态，避免重启结果长期覆盖额度读取状态。</summary>
    private void RestartNoticeTimerTick(object? sender, EventArgs e)
    {
        ClearRestartNotice();
    }

    /// <summary>停止一次性提示计时器并恢复原有绑定的连接状态区域，窗口关闭时也可安全重复调用。</summary>
    private void ClearRestartNotice()
    {
        StopRestartNoticeTimer();
        RestartNoticeTextBlock.Visibility = Visibility.Collapsed;
        RestartNoticeTextBlock.Text = string.Empty;
        RestartNoticeTextBlock.ToolTip = null;
        ConnectionStatusPanel.Visibility = Visibility.Visible;
    }

    /// <summary>解除提示计时器事件，防止连续重启操作保留多个计时器或在窗口销毁后更新控件。</summary>
    private void StopRestartNoticeTimer()
    {
        if (restartNoticeTimer is null)
        {
            return;
        }

        restartNoticeTimer.Stop();
        restartNoticeTimer.Tick -= RestartNoticeTimerTick;
        restartNoticeTimer = null;
    }

    /// <summary>将安全助手的内部状态映射为准确的本地化提示，成功只表示桌面端重启且恢复清单就绪，不表示流式会话已恢复。</summary>
    private string GetRestartResultMessage(CodexDesktopRestartResult result)
    {
        return result switch
        {
            CodexDesktopRestartResult.Relaunched => viewModel.Text.RestartCodexReady,
            CodexDesktopRestartResult.LoginRequired => viewModel.Text.RestartCodexLoginRequired,
            CodexDesktopRestartResult.NeedsManualClose => viewModel.Text.RestartCodexNeedsManualClose,
            CodexDesktopRestartResult.SnapshotUnavailable => viewModel.Text.RestartCodexSnapshotUnavailable,
            CodexDesktopRestartResult.TimedOut => viewModel.Text.RestartCodexTimedOut,
            _ => viewModel.Text.RestartCodexUnavailable
        };
    }

    /// <summary>切换到额度概览页；图标导航保留 RadioButton 的键盘和屏幕阅读器语义。</summary>
    private void DashboardRailChecked(object sender, RoutedEventArgs e)
    {
        if (DashboardContent is not null && SettingsContent is not null && DashboardRailButton.IsChecked == true)
        {
            DashboardContent.Visibility = Visibility.Visible;
            SettingsContent.Visibility = Visibility.Collapsed;
            RequestContentFit();
        }
    }

    /// <summary>切换到设置页；只由选中的导航项驱动内容，避免重复显示文字标签。</summary>
    private void SettingsRailChecked(object sender, RoutedEventArgs e)
    {
        if (DashboardContent is not null && SettingsContent is not null && SettingsRailButton.IsChecked == true)
        {
            DashboardContent.Visibility = Visibility.Collapsed;
            SettingsContent.Visibility = Visibility.Visible;
            RequestContentFit();
        }
    }

    /// <summary>记录用户主动拖拽后的尺寸；导航自适应不会覆盖用户已经选择的窗口大小。</summary>
    private void MainWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (autoSizingLocked && !suppressUserResizeTracking && (e.WidthChanged || e.HeightChanged))
        {
            userResized = true;
        }
    }

    /// <summary>面板切换后在下一帧重新测量内容，保留自由缩放并避免导航时出现大块空白。</summary>
    private void RequestContentFit()
    {
        if (!autoSizingLocked || userResized || !IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RecalculateContentSize));
    }

    /// <summary>临时恢复 SizeToContent 读取当前面板尺寸，再锁回手动模式。</summary>
    private void RecalculateContentSize()
    {
        if (!autoSizingLocked || userResized || !IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        suppressUserResizeTracking = true;
        try
        {
            Height = double.NaN;
            SizeToContent = SizeToContent.Height;
            UpdateLayout();
            var fittedWidth = ActualWidth;
            var fittedHeight = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            Width = fittedWidth;
            Height = fittedHeight;
            ClampToWorkArea();
        }
        finally
        {
            suppressUserResizeTracking = false;
        }
    }

    /// <summary>原生句柄就绪后开启背景模糊并应用已保存主题；不支持的系统使用同配色的实色渐变。</summary>
    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        WindowWorkArea.Attach(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var forceOpaque = Environment.GetCommandLineArgs().Contains("--opaque");
        backdropAvailable = !forceOpaque && WindowBackdrop.TryEnable(this, WidgetThemePalette.UsesDarkCaption(settings.Theme));
        ApplyThemeSurface();
    }

    /// <summary>循环夜间、白天和高透明玻璃主题，并立即保存用户选择以便下次启动保持同一外观。</summary>
    private void ThemeClicked(object sender, RoutedEventArgs e)
    {
        settings = settings with
        {
            Theme = settings.Theme switch
            {
                WidgetTheme.Night => WidgetTheme.Day,
                WidgetTheme.Day => WidgetTheme.Glass,
                _ => WidgetTheme.Night
            }
        };
        PersistSettings();
        ApplyThemeSurface();
    }

    /// <summary>同步主题资源、DWM 标题栏提示与主题按钮的文字说明，确保主题切换不依赖重新创建窗口。</summary>
    private void ApplyThemeSurface()
    {
        WidgetThemePalette.Apply(settings.Theme);
        viewModel.RefreshThemeBrushes();
        GlassSurface.Background = (System.Windows.Media.Brush)FindResource(WidgetThemePalette.GetSurfaceResourceKey(backdropAvailable));
        WindowBackdrop.ApplyCaptionTheme(this, WidgetThemePalette.UsesDarkCaption(settings.Theme));
        UpdateThemeButtonPresentation();
        UpdateRestartButtonPresentation();
    }

    /// <summary>用当前主题名称和“下一次点击的效果”更新无文字图标的悬浮提示及辅助功能名称。</summary>
    private void UpdateThemeButtonPresentation()
    {
        if (ThemeButton is null || ThemeGlyph is null || viewModel is null)
        {
            return;
        }

        var (name, tooltip, glyph) = settings.Theme switch
        {
            WidgetTheme.Day => (viewModel.Text.ThemeDay, viewModel.Text.ThemeDayTooltip, "M12,3 A9,9 0 1 0 21,12 A7,7 0 0 1 12,3 M12,1 V3 M12,21 V23 M1,12 H3 M21,12 H23"),
            WidgetTheme.Glass => (viewModel.Text.ThemeGlass, viewModel.Text.ThemeGlassTooltip, "M4,12 C4,7 8,4 12,4 C16,4 20,7 20,12 C20,17 16,20 12,20 C8,20 4,17 4,12 M7,12 H17 M12,7 V17"),
            _ => (viewModel.Text.ThemeNight, viewModel.Text.ThemeNightTooltip, "M15,3 A9,9 0 1 0 21,15 A7,7 0 0 1 15,3")
        };
        ThemeGlyph.Data = Geometry.Parse(glyph);
        ThemeButton.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(ThemeButton, name);
        System.Windows.Automation.AutomationProperties.SetHelpText(ThemeButton, tooltip);
    }

    /// <summary>切换当前会话置顶状态；高亮按钮与实际 Topmost 属性绑定，避免状态不一致。</summary>
    private void PinClicked(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
    }

    /// <summary>将窗口按当前内容恢复为紧凑大小后，吸附到最近角落并在工作区边缘保留两像素间距。</summary>
    private void SnapClicked(object sender, RoutedEventArgs e)
    {
        FitContentForSnap();
        WindowWorkArea.SnapToNearestCorner(this, 2);
    }

    /// <summary>显式贴边操作覆盖用户的手动窗口大小，按当前页面测量可容纳全部内容的尺寸，避免滚动条和大块空白并存。</summary>
    private void FitContentForSnap()
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        suppressUserResizeTracking = true;
        var originalScrollMode = ContentScrollViewer.VerticalScrollBarVisibility;
        try
        {
            // 贴角是用户明确要求的“恢复最优大小”，因此应撤销此前拖拽尺寸的优先级。
            userResized = false;
            ContentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            SizeToContent = SizeToContent.WidthAndHeight;
            UpdateLayout();
            var fittedWidth = Math.Max(MinWidth, Math.Ceiling(ActualWidth));
            var fittedHeight = Math.Max(MinHeight, Math.Ceiling(ActualHeight));
            SizeToContent = SizeToContent.Manual;
            Width = fittedWidth;
            Height = fittedHeight;
            WindowWorkArea.ClampSizeToCurrentWorkArea(this, 2);
        }
        finally
        {
            ContentScrollViewer.VerticalScrollBarVisibility = originalScrollMode;
            suppressUserResizeTracking = false;
        }
    }

    /// <summary>先锁定当前尺寸再最小化，避免启动异步读取在后台改写还原尺寸。</summary>
    private void MinimizeClicked(object sender, RoutedEventArgs e)
    {
        FinalizeAutoSizing();
        SystemCommands.MinimizeWindow(this);
    }

    /// <summary>切换最大化和还原；WindowChrome 保留原生工作区边界与还原矩形。</summary>
    private void MaximizeClicked(object sender, RoutedEventArgs e)
    {
        FinalizeAutoSizing();
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>关闭主窗口到托盘，继续额度刷新；完全退出仍由托盘的退出命令负责。</summary>
    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>同步最大化按钮图形和可访问名称；最大化/最小化不能触发自动尺寸回算。</summary>
    private void WindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph is null || viewModel is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(maximized ? "M8,5 H19 V16 M5,8 H16 V19 H5 Z" : "M5,5 H19 V19 H5 Z");
        var label = maximized ? viewModel.Text.RestoreWindow : viewModel.Text.MaximizeWindow;
        MaximizeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, label);
    }

    /// <summary>保存选定的刷新间隔，并让正在运行的 DispatcherTimer 不重启窗口即可生效。</summary>
    private void RefreshIntervalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingControls || RefreshIntervalComboBox.SelectedItem is not ComboBoxItem item
            || !int.TryParse(item.Tag?.ToString(), out var seconds))
        {
            return;
        }

        settings = settings with { RefreshIntervalSeconds = seconds };
        PersistSettings();
    }

    /// <summary>保存中英语言选择，并使所有绑定标题和当前会员时间文本立即重新格式化。</summary>
    private void LanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingControls || LanguageComboBox.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse<AppLanguage>(item.Tag?.ToString(), out var language))
        {
            return;
        }

        settings = settings with { Language = language };
        PersistSettings();
        WindowStateChanged(this, EventArgs.Empty);
        UpdateThemeButtonPresentation();
        UpdateRestartButtonPresentation();
    }

    /// <summary>将已保存设置同步回控件，避免语言切换或启动状态更新时触发递归保存。</summary>
    private void ApplySettingsToControls()
    {
        applyingControls = true;
        try
        {
            SelectItemByTag(RefreshIntervalComboBox, settings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            SelectItemByTag(LanguageComboBox, settings.Language.ToString());
        }
        finally
        {
            applyingControls = false;
        }

        SettingsStatusTextBlock.Text = string.Empty;
    }

    /// <summary>保存配置并更新 ViewModel，让当前界面即时反映新的刷新偏好。</summary>
    private void PersistSettings()
    {
        viewModel.ApplySettings(settings);
        saveSettings(settings);
        SettingsStatusTextBlock.Text = viewModel.Text.SettingsSaved;
        SettingsStatusTextBlock.ToolTip = viewModel.Text.SettingsSaved;
    }

    /// <summary>选择 Tag 匹配的下拉项，避免依赖中英文显示文本而导致设置恢复失败。</summary>
    private static void SelectItemByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    /// <summary>首次显示时仍贴近主屏幕右下角，既保留小组件可见性，也为完整到期时间预留足够宽度。</summary>
    private void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 12;
        Top = workArea.Bottom - ActualHeight - 12;

        if (startOnSettings)
        {
            SettingsRailButton.IsChecked = true;
        }

        // 等首个真实额度快照完成后再锁定初始尺寸；若登录状态无法返回数据，超时兜底仍会开放自由拖拽。
        autoSizingFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        autoSizingFallbackTimer.Tick += AutoSizingFallbackTimerTick;
        autoSizingFallbackTimer.Start();

        if (!string.IsNullOrWhiteSpace(captureScreenshotPath))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(CaptureScreenshot));
        }
    }

    /// <summary>在显式测试参数下从 WPF 视觉树生成 PNG，绕过无边框窗口在 PrintWindow 下的 DWM 空白限制。</summary>
    private void CaptureScreenshot()
    {
        if (string.IsNullOrWhiteSpace(captureScreenshotPath))
        {
            return;
        }

        if (!startOnSettings && !viewModel.HasDisplayableData && captureAttempts++ < 30)
        {
            ScheduleCaptureRetry();
            return;
        }

        UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var fullPath = Path.GetFullPath(captureScreenshotPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var stream = File.Create(fullPath))
        {
            encoder.Save(stream);
        }

        if (exitOnClose)
        {
            RequestApplicationExit();
        }
    }

    /// <summary>等待演示额度快照完成再截图，避免异步首帧把卡片误判为空。</summary>
    private void ScheduleCaptureRetry()
    {
        if (captureRetryTimer is not null)
        {
            return;
        }

        captureRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        captureRetryTimer.Tick += CaptureRetryTimerTick;
        captureRetryTimer.Start();
    }

    /// <summary>执行一次延迟截图并释放重试计时器。</summary>
    private void CaptureRetryTimerTick(object? sender, EventArgs e)
    {
        if (captureRetryTimer is not null)
        {
            captureRetryTimer.Stop();
            captureRetryTimer.Tick -= CaptureRetryTimerTick;
            captureRetryTimer = null;
        }

        CaptureScreenshot();
    }

    /// <summary>首个额度快照完成后锁定内容驱动的初始尺寸，随后允许用户自由调整窗口。</summary>
    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (autoSizingLocked || e.PropertyName != string.Empty || !viewModel.HasDisplayableData)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FinalizeAutoSizing));
    }

    /// <summary>无论真实额度是否可用，最多等待三秒后也开放手动缩放，避免错误状态下窗口被永久锁定。</summary>
    private void AutoSizingFallbackTimerTick(object? sender, EventArgs e)
    {
        FinalizeAutoSizing();
    }

    /// <summary>保存当前内容测量结果并关闭 SizeToContent，确保后续 ResizeMode=CanResize 真正生效。</summary>
    private void FinalizeAutoSizing()
    {
        if (autoSizingLocked || !IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        autoSizingLocked = true;
        if (autoSizingFallbackTimer is not null)
        {
            autoSizingFallbackTimer.Stop();
            autoSizingFallbackTimer.Tick -= AutoSizingFallbackTimerTick;
            autoSizingFallbackTimer = null;
        }

        suppressUserResizeTracking = true;
        try
        {
            var fittedWidth = ActualWidth;
            var fittedHeight = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            Width = fittedWidth;
            Height = fittedHeight;
            userResized = false;
            ClampToWorkArea();
        }
        finally
        {
            suppressUserResizeTracking = false;
        }
    }

    /// <summary>在内容自适应后把窗口限制在工作区内，避免完整到期时间导致右下角被屏幕裁掉。</summary>
    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 8, Math.Min(Left, workArea.Right - Width - 8));
        Top = Math.Max(workArea.Top + 8, Math.Min(Top, workArea.Bottom - Height - 8));
    }

    /// <summary>关闭主界面时隐藏到托盘；发布验收的 <c>--exit-on-close</c> 参数允许自动化测试验证正常退出。</summary>
    private void MainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!exitRequested && !exitOnClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>仅在明确退出后释放刷新计时器和取消令牌；发布验收参数还会结束显式关闭模式下的应用进程。</summary>
    private void MainWindowClosed(object? sender, EventArgs e)
    {
        viewModel.PropertyChanged -= ViewModelPropertyChanged;
        captureRetryTimer?.Stop();
        autoSizingFallbackTimer?.Stop();
        StopRestartNoticeTimer();
        restartLifetime.Cancel();
        restartLifetime.Dispose();
        viewModel.Dispose();
        if (exitOnClose)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
