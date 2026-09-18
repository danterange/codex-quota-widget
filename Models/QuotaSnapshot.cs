namespace CodexQuotaWidget.Models;

/// <summary>
/// 描述一个额度时间窗口的展示数据；Percent 使用 0 到 100 的范围。
/// </summary>
public sealed record QuotaWindow(string Label, double Percent, DateTimeOffset ResetAt);

/// <summary>
/// 描述悬浮窗当前账号需要展示的完整快照。
/// </summary>
public sealed record QuotaSnapshot(
    string AccountLabel,
    string PlanName,
    QuotaWindow FiveHour,
    QuotaWindow SevenDay,
    DateTimeOffset MembershipExpiresAt,
    int ResetCredits);
