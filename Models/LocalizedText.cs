namespace CodexQuotaWidget.Models;

/// <summary>
/// 汇集主界面与托盘会使用的本地化文本，避免界面语言切换时遗漏独立菜单项。
/// </summary>
public sealed record LocalizedText(
    string WindowTitle,
    string MonitorSubtitle,
    string LiveStatus,
    string DashboardTab,
    string SettingsTab,
    string RefreshNow,
    string RefreshInterval,
    string Interval5Seconds,
    string Interval10Seconds,
    string Interval30Seconds,
    string Interval60Seconds,
    string Interval120Seconds,
    string Language,
    string SimplifiedChinese,
    string English,
    string OpenMainWindow,
    string ExitWidget,
    string SettingsSaved,
    string RefreshingQuota,
    string Seconds,
    string AutoRefreshSuffix,
    string LoadingMembership,
    string MembershipUnknown,
    string MembershipExpired,
    string ResetCredits);

/// <summary>按用户保存的语言返回一组完整界面文本，避免混用中英文控件标题。</summary>
public static class LocalizedTextProvider
{
    /// <summary>为指定语言生成主界面、设置页和托盘菜单共同使用的文本。</summary>
    public static LocalizedText Get(AppLanguage language)
    {
        return language == AppLanguage.English
            ? new LocalizedText(
                "Codex Quota Widget",
                "QUOTA MONITOR",
                "LIVE",
                "Dashboard",
                "Settings",
                "Refresh now",
                "Refresh interval",
                "5 seconds",
                "10 seconds",
                "30 seconds",
                "60 seconds",
                "120 seconds",
                "Language",
                "简体中文",
                "English",
                "Open main window",
                "Exit widget",
                "Settings saved",
                "Refreshing quota…",
                "seconds",
                "automatic refresh; percentages show remaining quota.",
                "Loading membership expiry…",
                "Membership expiry unavailable",
                "Expired",
                "Reset credits")
            : new LocalizedText(
                "Codex 额度",
                "额度监控",
                "运行中",
                "额度",
                "设置",
                "刷新额度",
                "刷新间隔",
                "5 秒",
                "10 秒",
                "30 秒",
                "60 秒",
                "120 秒",
                "界面语言",
                "简体中文",
                "English",
                "主界面",
                "退出小组件",
                "设置已保存",
                "正在读取额度…",
                "秒",
                "自动刷新；百分比表示剩余额度。",
                "正在读取会员到期时间…",
                "会员到期时间未知",
                "已到期",
                "重置次数");
    }
}
