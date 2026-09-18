using CodexQuotaWidget.Models;

namespace CodexQuotaWidget.Services;

/// <summary>
/// 为界面提供当前账号额度快照；真实接口接入时替换该边界即可保持 UI 不变。
/// </summary>
public interface IQuotaProvider
{
    /// <summary>
    /// 异步读取当前账号的额度和会员信息。
    /// </summary>
    Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 提供本地演示数据，使小组件在没有远端额度接口时仍能完整运行和截图验证。
/// </summary>
public sealed class DemoQuotaProvider : IQuotaProvider
{
    /// <summary>
    /// 返回固定的演示额度，并把重置时间设为相对当前时间，确保倒计时可观察。
    /// </summary>
    public Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        var snapshot = new QuotaSnapshot(
            "a***@example.com",
            "Pro",
            new QuotaWindow("5h", 42, now.AddHours(1).AddMinutes(26)),
            new QuotaWindow("7d", 68, now.AddDays(4).AddHours(8)),
            new DateTimeOffset(now.Year, 10, 12, 23, 59, 0, now.Offset),
            1);

        return Task.FromResult(snapshot);
    }
}
