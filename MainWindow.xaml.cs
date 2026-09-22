using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>初始化数据绑定、设置控件和开机启动状态，避免首次显示时触发无意义的保存事件，并识别发布验收的正常退出参数。</summary>
    internal MainWindow(WidgetSettings initialSettings, Func<WidgetSettings, AutoStartResult> saveSettingsCallback, AutoStartResult autoStartResult)
    {
        InitializeComponent();
        settings = initialSettings;
        saveSettings = saveSettingsCallback;
        exitOnClose = Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--exit-on-close", StringComparison.OrdinalIgnoreCase));
        viewModel = new QuotaViewModel(settings: settings);
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
        viewModel.Dispose();
        if (exitOnClose)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
