using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CodexQuotaWidget.Models;
using CodexQuotaWidget.Services;

namespace CodexQuotaWidget.ViewModels;

/// <summary>
/// 将额度快照转换为紧凑文本和进度值，并按十秒刷新倒计时显示。
/// </summary>
public sealed class QuotaViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IQuotaProvider quotaProvider;
    private readonly DispatcherTimer refreshTimer;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private QuotaSnapshot? snapshot;
    private string? errorMessage;
    private string errorDetail = string.Empty;
    private AppLanguage language;
    private DateTimeOffset? manualMembershipExpiresAt;
    private bool refreshing;
    private bool disposed;

    /// <summary>
    /// 初始化真实 Codex 提供器；只有显式传入演示提供器时才使用静态演示数据。
    /// </summary>
    public QuotaViewModel(IQuotaProvider? provider = null, int refreshIntervalSeconds = 10, WidgetSettings? settings = null)
    {
        var initialSettings = settings ?? new WidgetSettings(RefreshIntervalSeconds: refreshIntervalSeconds);
        quotaProvider = provider ?? CreateDefaultProvider();
        language = initialSettings.Language;
        manualMembershipExpiresAt = initialSettings.ManualMembershipExpiresAt;
        RefreshIntervalSeconds = Math.Clamp(initialSettings.RefreshIntervalSeconds, 1, 3600);
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        refreshTimer.Tick += RefreshTimerTick;
        _ = RefreshAsync();
        refreshTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Visibility FiveHourVisibility => snapshot?.FiveHour is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SevenDayVisibility => snapshot?.SevenDay is null ? Visibility.Collapsed : Visibility.Visible;
    public double FiveHourPercent => snapshot?.FiveHour?.Percent ?? 0;
    public string FiveHourPercentText => FormatPercent(snapshot?.FiveHour);
    public string FiveHourResetText => FormatRemaining(snapshot?.FiveHour?.ResetAt);
    public double SevenDayPercent => snapshot?.SevenDay?.Percent ?? 0;
    public string SevenDayPercentText => FormatPercent(snapshot?.SevenDay);
    public string SevenDayResetText => FormatRemaining(snapshot?.SevenDay?.ResetAt);
    public string PlanName => snapshot?.PlanName is { Length: > 0 } plan ? plan : "--";
    public LocalizedText Text => LocalizedTextProvider.Get(language);
    public string MembershipText => errorMessage is not null
        ? LocalizeError(errorMessage)
        : GetMembershipExpiry() is { } expiry
        ? FormatMembershipExpiry(expiry, DateTimeOffset.Now, language)
        : snapshot is null ? Text.LoadingMembership : Text.MembershipUnknown;
    public string ResetCreditsText => snapshot?.ResetCredits is { } count ? count.ToString() : "--";
    public string ResetCreditsDisplayText => $"{Text.ResetCredits}: {ResetCreditsText}";
    public string ErrorText => errorDetail;
    public int RefreshIntervalSeconds { get; private set; }
    public string StatusText => errorMessage is not null
        ? LocalizeErrorDetail(errorDetail) + (snapshot is null ? "" : language == AppLanguage.English ? " The last successful quota remains visible." : " 当前额度保留上次成功结果。")
        : refreshing ? Text.RefreshingQuota : $"{Text.RefreshInterval}: {RefreshIntervalSeconds} {Text.Seconds} {Text.AutoRefreshSuffix}";

    /// <summary>更新自动刷新间隔并立即作用于后续计时，不启动并行读取。</summary>
    public void SetRefreshIntervalSeconds(int seconds)
    {
        var normalized = Math.Clamp(seconds, 1, 3600);
        if (normalized == RefreshIntervalSeconds)
        {
            return;
        }

        RefreshIntervalSeconds = normalized;
        refreshTimer.Interval = TimeSpan.FromSeconds(normalized);
        OnPropertyChanged(nameof(RefreshIntervalSeconds));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// 应用设置页保存的偏好，使语言、手动会员到期兜底和刷新间隔在不重启窗口的情况下立即生效。
    /// </summary>
    public void ApplySettings(WidgetSettings settings)
    {
        var languageChanged = language != settings.Language;
        language = settings.Language;
        manualMembershipExpiresAt = settings.ManualMembershipExpiresAt;
        SetRefreshIntervalSeconds(settings.RefreshIntervalSeconds);

        if (languageChanged)
        {
            // Text 是嵌套绑定对象，通知其整体变化才能刷新所有标题与菜单相关文本。
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(FiveHourResetText));
            OnPropertyChanged(nameof(SevenDayResetText));
            OnPropertyChanged(nameof(ResetCreditsDisplayText));
        }

        OnPropertyChanged(nameof(MembershipText));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// 暴露一次手动刷新入口，复用与定时刷新相同的错误处理和状态通知。
    /// </summary>
    public Task RefreshNowAsync()
    {
        return RefreshAsync();
    }

    /// <summary>
    /// 停止定时器并取消进行中的读取，窗口关闭后不再更新绑定。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        refreshTimer.Tick -= RefreshTimerTick;
        refreshTimer.Stop();
        lifetime.Cancel();
        if (!refreshing)
        {
            lifetime.Dispose();
            refreshGate.Dispose();
        }
    }

    /// <summary>
    /// 从额度提供器加载快照；读取失败时保留已有快照并暴露错误状态。
    /// </summary>
    private async Task RefreshAsync()
    {
        if (disposed || !await refreshGate.WaitAsync(0))
        {
            return;
        }

        refreshing = true;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            try
            {
                snapshot = await quotaProvider.GetSnapshotAsync(lifetime.Token);
                errorMessage = null;
                errorDetail = string.Empty;
            }
            catch (OperationCanceledException) when (disposed)
            {
                return;
            }
            catch (QuotaReadException exception)
            {
                errorMessage = exception.Message;
                errorDetail = exception.Detail;
            }
            catch (OperationCanceledException)
            {
                errorMessage = "读取超时";
                errorDetail = "额度读取被取消或超时，组件会自动重试。";
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException or FormatException or OverflowException or Win32Exception or UnauthorizedAccessException or ArgumentException or System.Collections.Generic.KeyNotFoundException)
            {
                errorMessage = "额度读取失败";
                errorDetail = "Codex 启动、通信或响应解析失败，请检查安装和网络后重试。";
            }
        }
        finally
        {
            refreshing = false;
            refreshGate.Release();
            if (disposed)
            {
                lifetime.Dispose();
                refreshGate.Dispose();
            }
            else
            {
                OnPropertyChanged(string.Empty);
            }
        }
    }

    /// <summary>
    /// 在 UI 线程周期性刷新倒计时文本；额度本身由提供器按需重新读取。
    /// </summary>
    private void RefreshTimerTick(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(FiveHourResetText));
        OnPropertyChanged(nameof(SevenDayResetText));
        OnPropertyChanged(nameof(MembershipText));
        _ = RefreshAsync();
    }

    /// <summary>
    /// 根据启动参数选择真实提供器或明确的本地演示提供器。
    /// </summary>
    private static IQuotaProvider CreateDefaultProvider()
    {
        var useDemo = Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase));
        return useDemo ? new DemoQuotaProvider() : new CodexQuotaProvider();
    }

    /// <summary>
    /// 将可选额度窗口转换为百分比文本，未知窗口保持占位符而不伪造数值。
    /// </summary>
    private static string FormatPercent(QuotaWindow? quotaWindow)
    {
        return quotaWindow is null ? "--" : $"{quotaWindow.Percent:0}%";
    }

    /// <summary>
    /// 将未来的额度重置时间按当前界面语言压缩为天、小时、分钟文本，避免 English 界面残留中文“现在”。
    /// </summary>
    private string FormatRemaining(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return "--";
        }

        var remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return language == AppLanguage.English ? "now" : "现在";
        }

        if (remaining.TotalDays >= 1)
        {
            return language == AppLanguage.English
                ? $"{(int) remaining.TotalDays}d {remaining.Hours:00}h"
                : $"{(int) remaining.TotalDays}天{remaining.Hours:00}小时";
        }

        return language == AppLanguage.English
            ? $"{(int) remaining.TotalHours}h {remaining.Minutes:00}m"
            : $"{(int) remaining.TotalHours}小时{remaining.Minutes:00}分";
    }

    /// <summary>将提供器预定义的错误标题映射为当前界面语言，避免英语界面夹杂中文错误名称。</summary>
    private string LocalizeError(string message)
    {
        if (language != AppLanguage.English)
        {
            return message;
        }

        return message switch
        {
            "未找到 Codex"       => "Codex not found",
            "Codex 启动失败"     => "Codex could not start",
            "请先登录 Codex"     => "Sign in to Codex first",
            "需要 ChatGPT 登录"  => "ChatGPT sign-in required",
            "读取超时"           => "Read timed out",
            "Codex 安装不完整"   => "Codex installation is incomplete",
            "登录已失效"         => "Sign-in expired",
            "额度接口请求失败"   => "Quota request failed",
            "暂无额度数据"       => "No quota data",
            "额度读取失败"       => "Quota read failed",
            _                    => message
        };
    }

    /// <summary>翻译不含账户或服务端原文的固定错误详情，保留底层隐私保护边界。</summary>
    private string LocalizeErrorDetail(string detail)
    {
        if (language != AppLanguage.English)
        {
            return detail;
        }

        return detail switch
        {
            "请安装并登录 Codex 桌面应用或 CLI，再刷新额度。"                 => "Install and sign in to Codex Desktop or CLI, then refresh.",
            "请检查 Codex 安装是否完整。"                                   => "Check that the Codex installation is complete.",
            "请在 Codex 中登录 ChatGPT 账号，组件将在下一次刷新时重试。"     => "Sign in to your ChatGPT account in Codex. The widget will retry.",
            "API Key 登录不提供 Plus / Pro 的订阅额度。"                     => "API-key sign-in does not provide Plus or Pro subscription quotas.",
            "8 秒内未收到额度响应，请检查网络；组件会自动重试。"             => "No quota response arrived within 8 seconds. Check the network; the widget will retry.",
            "npm 版缺少平台依赖；可安装并登录 Codex 桌面版，或修复 CLI 安装。" => "The npm CLI is missing a platform dependency. Install Codex Desktop or repair the CLI.",
            "请在 Codex 中重新登录 ChatGPT 账号，然后刷新额度。"             => "Sign in to your ChatGPT account in Codex again, then refresh.",
            "Codex 未能获取额度，请检查网络和登录状态；组件会自动重试。"     => "Codex could not fetch quota data. Check the network and sign-in; the widget will retry.",
            "Codex 未返回 5 小时或 7 天窗口，组件将自动重试。"               => "Codex returned neither a 5-hour nor 7-day window. The widget will retry.",
            "Codex 启动、通信或响应解析失败，请检查安装和网络后重试。"       => "Codex startup, communication, or response parsing failed. Check installation and network, then retry.",
            "额度读取被取消或超时，组件会自动重试。"                         => "The quota read was cancelled or timed out. The widget will retry.",
            _                                                              => detail
        };
    }

    /// <summary>自动数据存在时优先展示；只有协议未提供到期时间才使用用户保存的本地兜底日期。</summary>
    private DateTimeOffset? GetMembershipExpiry()
    {
        return SelectMembershipExpiry(snapshot?.MembershipExpiresAt, manualMembershipExpiresAt);
    }

    /// <summary>选择协议明确提供的自动到期时间；只有它缺失时才允许本地手动日期作为显示兜底。</summary>
    internal static DateTimeOffset? SelectMembershipExpiry(DateTimeOffset? automaticExpiry, DateTimeOffset? manualExpiry)
    {
        return automaticExpiry ?? manualExpiry;
    }

    /// <summary>
    /// 按指定语言和参考时刻生成会员到期文本；剩余时间向下取整到分钟，保证中文格式与用户指定的天、时、分表达一致。
    /// </summary>
    public static string FormatMembershipExpiry(DateTimeOffset expiry, DateTimeOffset now, AppLanguage language)
    {
        var localExpiry = expiry.ToLocalTime();
        var localNow = now.ToLocalTime();
        var remaining = localExpiry - localNow;
        if (remaining <= TimeSpan.Zero)
        {
            return language == AppLanguage.English
                ? $"Expired: {localExpiry:yyyy/MM/dd HH:mm:ss}"
                : $"已到期：{localExpiry:yyyy/MM/dd HH:mm:ss}";
        }

        var days = (int) remaining.TotalDays;
        return language == AppLanguage.English
            ? $"Expires: {localExpiry:yyyy/MM/dd HH:mm:ss} ({days}d {remaining.Hours}h {remaining.Minutes}m left)"
            : $"到期：{localExpiry:yyyy/MM/dd HH:mm:ss}(剩余{days}天{remaining.Hours}小时{remaining.Minutes}分)";
    }

    /// <summary>
    /// 统一触发属性变化通知，保证异步加载后的派生文本同步更新。
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
