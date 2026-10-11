namespace CodexQuotaWidget.Models;

/// <summary>
/// 描述一个额度时间窗口的展示数据；Percent 使用 0 到 100 的范围，表示剩余额度。
/// </summary>
public sealed record QuotaWindow(string Label, double Percent, DateTimeOffset? ResetAt);

/// <summary>
/// 描述悬浮窗当前账号需要展示的完整快照。
/// </summary>
public sealed record QuotaSnapshot(
    string AccountLabel,
    string PlanName,
    QuotaWindow? FiveHour,
    QuotaWindow? SevenDay,
    DateTimeOffset? MembershipExpiresAt,
    int? ResetCredits)
{
    /// <summary>30 天/月度额度窗口；Free 账户可只返回此窗口而没有 5 小时或 7 天窗口。</summary>
    public QuotaWindow? Monthly { get; init; }

    /// <summary>账户可消费剩余额度；与可用重置券数量是两类不同的数值。</summary>
    public decimal? RemainingCredits { get; init; }

    /// <summary>明确的无限额度标记；为 true 时不显示虚构数值，改为展示“不限”。</summary>
    public bool CreditsUnlimited { get; init; }

    /// <summary>可用重置券中最早的到期时间；只作提醒展示，组件不提供或调用重置操作。</summary>
    public DateTimeOffset? EarliestResetCreditExpiresAt { get; init; }

    /// <summary>
    /// 标识当前账号的非敏感键；通常为 app-server 返回的 accountId，不应存放令牌或完整邮箱。
    /// </summary>
    public string AccountKey { get; init; } = string.Empty;
}
