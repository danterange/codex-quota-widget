using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CodexQuotaWidget.Models;
using CodexQuotaWidget.Services;

namespace CodexQuotaWidget.ViewModels;

/// <summary>
/// 将额度快照转换为紧凑文本和进度值，并按分钟刷新倒计时显示。
/// </summary>
public sealed class QuotaViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IQuotaProvider quotaProvider = new DemoQuotaProvider();
    private readonly DispatcherTimer refreshTimer;
    private QuotaSnapshot? snapshot;
    private bool disposed;

    /// <summary>
    /// 初始化演示数据提供器和轻量级倒计时刷新器。
    /// </summary>
    public QuotaViewModel()
    {
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        refreshTimer.Tick += RefreshTimerTick;
        _ = RefreshAsync();
        refreshTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double FiveHourPercent => snapshot?.FiveHour.Percent ?? 0;
    public string FiveHourPercentText => $"{FiveHourPercent:0}%";
    public string FiveHourResetText => FormatRemaining(snapshot?.FiveHour.ResetAt);
    public double SevenDayPercent => snapshot?.SevenDay.Percent ?? 0;
    public string SevenDayPercentText => $"{SevenDayPercent:0}%";
    public string SevenDayResetText => FormatRemaining(snapshot?.SevenDay.ResetAt);
    public string PlanName => snapshot?.PlanName ?? "Pro";
    public int ResetCredits => snapshot?.ResetCredits ?? 0;

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
    /// 从额度提供器加载快照，并一次性通知界面刷新所有派生属性。
    /// </summary>
    private async Task RefreshAsync()
    {
        try
        {
            snapshot = await quotaProvider.GetSnapshotAsync(CancellationToken.None);
            OnPropertyChanged(string.Empty);
        }
        catch (OperationCanceledException)
        {
            // 取消只终止本次刷新，不把窗口状态误报为失败。
        }
    }

    /// <summary>
    /// 在 UI 线程周期性刷新倒计时文本；额度数据本身由后续真实提供器按需替换。
    /// </summary>
    private void RefreshTimerTick(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(FiveHourResetText));
        OnPropertyChanged(nameof(SevenDayResetText));
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
