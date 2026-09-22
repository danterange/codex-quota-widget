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
    private readonly Func<WidgetSettings, AutoStartResult> saveSettings;
    private WidgetSettings settings;
    private bool applyingControls;
    private bool exitRequested;
    private readonly bool exitOnClose;
    private readonly string? captureScreenshotPath;
    private readonly bool startOnSettings;
    private bool autoSizingLocked;
    private bool userResized;
    private bool suppressUserResizeTracking;
    private int captureAttempts;
    private DispatcherTimer? captureRetryTimer;
    private DispatcherTimer? autoSizingFallbackTimer;

    /// <summary>初始化数据绑定、设置控件和开机启动状态，避免首次显示时触发无意义的保存事件，并识别发布验收的正常退出参数。</summary>
    internal MainWindow(WidgetSettings initialSettings, Func<WidgetSettings, AutoStartResult> saveSettingsCallback, AutoStartResult autoStartResult)
    {
        InitializeComponent();
        settings = initialSettings;
        saveSettings = saveSettingsCallback;
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
        ApplySettingsToControls(autoStartResult);
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
        if (!autoSizingLocked || userResized || !IsLoaded)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RecalculateContentSize));
    }

    /// <summary>临时恢复 SizeToContent 读取当前面板尺寸，再锁回手动模式。</summary>
    private void RecalculateContentSize()
    {
        if (!autoSizingLocked || userResized || !IsLoaded)
        {
            return;
        }

        suppressUserResizeTracking = true;
        try
        {
            Width = double.NaN;
            Height = double.NaN;
            SizeToContent = SizeToContent.WidthAndHeight;
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

    /// <summary>允许无系统标题栏窗口通过深色自绘标题区域移动，避免恢复系统白色标题栏。</summary>
    private void WindowHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
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
    }

    /// <summary>保存当前用户是否登录 Windows 后启动组件；失败时保留设置页可理解的反馈。</summary>
    private void LaunchAtLoginChanged(object sender, RoutedEventArgs e)
    {
        if (applyingControls)
        {
            return;
        }

        settings = settings with { LaunchAtLogin = LaunchAtLoginCheckBox.IsChecked == true };
        PersistSettings();
    }

    /// <summary>
    /// 校验用户输入的本地到期兜底日期；空值明确表示清除兜底而不是把当前时间误写为到期时间。
    /// </summary>
    private void SaveMembershipExpiryClicked(object sender, RoutedEventArgs e)
    {
        var raw = MembershipExpiryTextBox.Text.Trim();
        if (raw.Length == 0)
        {
            settings = settings with { ManualMembershipExpiresAt = null };
            PersistSettings();
            return;
        }

        if (!DateTimeOffset.TryParseExact(raw, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var expiry))
        {
            SettingsStatusTextBlock.Text = settings.Language == AppLanguage.English
                ? "Use yyyy/MM/dd HH:mm:ss."
                : "请输入 yyyy/MM/dd HH:mm:ss。";
            return;
        }

        settings = settings with { ManualMembershipExpiresAt = expiry.ToLocalTime() };
        PersistSettings();
    }

    /// <summary>将已保存设置同步回控件，避免语言切换或启动状态更新时触发递归保存。</summary>
    private void ApplySettingsToControls(AutoStartResult autoStartResult)
    {
        applyingControls = true;
        try
        {
            SelectItemByTag(RefreshIntervalComboBox, settings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            SelectItemByTag(LanguageComboBox, settings.Language.ToString());
            LaunchAtLoginCheckBox.IsChecked = settings.LaunchAtLogin;
            MembershipExpiryTextBox.Text = settings.ManualMembershipExpiresAt is { } expiry
                ? expiry.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)
                : string.Empty;
        }
        finally
        {
            applyingControls = false;
        }

        ShowSaveResult(autoStartResult, showSuccess: false);
    }

    /// <summary>保存配置、更新 ViewModel 并显示注册表启动项实际是否可用的结果。</summary>
    private void PersistSettings()
    {
        viewModel.ApplySettings(settings);
        var autoStartResult = saveSettings(settings);
        ShowSaveResult(autoStartResult, showSuccess: true);
    }

    /// <summary>选择 Tag 匹配的下拉项，避免依赖中英文显示文本而导致设置恢复失败。</summary>
    private static void SelectItemByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    /// <summary>把启动项结果转换成当前界面语言的短提示，底层异常仅在可诊断时显示。</summary>
    private void ShowSaveResult(AutoStartResult autoStartResult, bool showSuccess)
    {
        if (!autoStartResult.IsAvailable)
        {
            SettingsStatusTextBlock.Text = viewModel.Text.AutoStartUnavailable;
            return;
        }

        if (!autoStartResult.Succeeded)
        {
            SettingsStatusTextBlock.Text = autoStartResult.Detail ?? viewModel.Text.AutoStartUnavailable;
            return;
        }

        SettingsStatusTextBlock.Text = showSuccess ? viewModel.Text.SettingsSaved : string.Empty;
    }

    /// <summary>首次显示时仍贴近主屏幕右下角，既保留小组件可见性，也为完整到期时间预留足够宽度。</summary>
    private void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;

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

        if (!startOnSettings && viewModel.PlanName == "--" &&
            viewModel.FiveHourVisibility == Visibility.Collapsed &&
            viewModel.SevenDayVisibility == Visibility.Collapsed && captureAttempts++ < 30)
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
        if (autoSizingLocked || e.PropertyName != string.Empty || (viewModel.PlanName == "--" && viewModel.FiveHourVisibility == Visibility.Collapsed && viewModel.SevenDayVisibility == Visibility.Collapsed))
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
        if (autoSizingLocked || !IsLoaded)
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
        viewModel.Dispose();
        if (exitOnClose)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
