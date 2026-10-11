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
/// 表示小组件可持久化的外观方案；枚举值会写入本地设置文件，新增或重命名时必须保留旧值的兼容处理。
/// </summary>
public enum WidgetTheme
{
    /// <summary>默认的深色夜间方案，优先保证夜间桌面上的文字和状态对比度。</summary>
    Night,

    /// <summary>高可读性的明亮方案，适合浅色桌面或白天环境。</summary>
    Day,

    /// <summary>透明度更高的玻璃方案；系统不支持 DWM 材质时会安全降级为可读的深色表面。</summary>
    Glass
}

/// <summary>
/// 保存不会包含账号凭据的当前用户偏好；手动到期时间只在 Codex 未返回自动数据时作为显示兜底。
/// </summary>
public sealed record WidgetSettings(
    int RefreshIntervalSeconds = 10,
    AppLanguage Language = AppLanguage.SimplifiedChinese,
    WidgetTheme Theme = WidgetTheme.Night);
