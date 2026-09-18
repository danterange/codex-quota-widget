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
    private QuotaSnapshot? snapshot;
    private string? errorMessage;
    private bool disposed;

    /// <summary>
    /// 初始化真实 Codex 提供器；只有显式传入演示提供器时才使用静态演示数据。
    /// </summary>
    public QuotaViewModel(IQuotaProvider? provider = null)
    {
        quotaProvider = provider ?? CreateDefaultProvider();
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
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
    public string MembershipText => errorMessage is not null
        ? errorMessage
        : snapshot?.MembershipExpiresAt is { } expiry
        ? $"会员至 {expiry:MM/dd} · 重置"
        : "会员期限未知 · 重置";
    public string ResetCreditsText => snapshot?.ResetCredits is { } count ? count.ToString() : "--";
    public string ErrorText => errorMessage ?? string.Empty;

    /// <summary>
    /// 暴露一次手动刷新入口，复用与定时刷新相同的错误处理和状态通知。
    /// </summary>
    public Task RefreshNowAsync()
    {
        return RefreshAsync();
    }

    /// <summary>
    /// 释放计时器事件，避免窗口关闭后仍保留 UI 线程回调。
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
    }

    /// <summary>
    /// 从额度提供器加载快照；读取失败时保留已有快照并暴露错误状态。
    /// </summary>
    private async Task RefreshAsync()
    {
        if (!await refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            try
            {
                snapshot = await quotaProvider.GetSnapshotAsync(CancellationToken.None);
                errorMessage = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException or Win32Exception)
            {
                errorMessage = "额度读取失败";
            }

            OnPropertyChanged(string.Empty);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>
    /// 在 UI 线程周期性刷新倒计时文本；额度本身由提供器按需重新读取。
    /// </summary>
    private void RefreshTimerTick(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(FiveHourResetText));
        OnPropertyChanged(nameof(SevenDayResetText));
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
    /// 将未来时间压缩为小组件可容纳的小时或天小时格式。
    /// </summary>
    private static string FormatRemaining(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return "--";
        }

        var remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "现在";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int) remaining.TotalDays}d{remaining.Hours:00}h";
        }

        return $"{(int) remaining.TotalHours}:{remaining.Minutes:00}";
    }

    /// <summary>
    /// 统一触发属性变化通知，保证异步加载后的派生文本同步更新。
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
