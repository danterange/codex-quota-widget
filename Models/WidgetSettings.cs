namespace CodexQuotaWidget.Models;

/// <summary>
/// 表示小组件界面使用的语言；该值只影响本地显示文本，不会改变 Codex 账号或服务器数据。
/// </summary>
public enum AppLanguage
{
    /// <summary>使用简体中文，作为首次运行时的默认界面语言。</summary>
    SimplifiedChinese,

    /// <summary>使用英语，便于在英文 Windows 环境中查看设置和状态。</summary>
    English
}

/// <summary>
/// 保存不会包含账号凭据的当前用户偏好；手动到期时间只在 Codex 未返回自动数据时作为显示兜底。
/// </summary>
public sealed record WidgetSettings(
    int RefreshIntervalSeconds = 10,
    AppLanguage Language = AppLanguage.SimplifiedChinese,
    bool LaunchAtLogin = true,
    DateTimeOffset? ManualMembershipExpiresAt = null);
