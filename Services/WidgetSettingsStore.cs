using System.IO;
using System.Text.Json;
using CodexQuotaWidget.Models;

namespace CodexQuotaWidget.Services;

/// <summary>读写当前 Windows 用户的小组件偏好，不触碰 Codex 认证数据。</summary>
internal static class WidgetSettingsStore
{
    private const int DefaultRefreshIntervalSeconds = 10;

    /// <summary>加载配置；文件缺失、损坏或间隔越界时回退到十秒。</summary>
    internal static WidgetSettings Load(string? path = null)
    {
        var settingsPath = path ?? GetDefaultPath();
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new WidgetSettings();
            }

            var settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(settingsPath));
            return Normalize(settings ?? new WidgetSettings());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WidgetSettings();
        }
    }

    /// <summary>保存配置；写入失败不影响额度读取和小组件运行。</summary>
    internal static void Save(WidgetSettings settings, string? path = null)
    {
        var settingsPath = path ?? GetDefaultPath();
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(settingsPath, JsonSerializer.Serialize(Normalize(settings)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 偏好属于可选持久化，不能阻断当前会话的刷新间隔修改。
        }
    }

    /// <summary>限制刷新间隔范围，防止误填零秒造成 UI 线程高频请求。</summary>
    internal static WidgetSettings Normalize(WidgetSettings settings)
    {
        return settings with { RefreshIntervalSeconds = Math.Clamp(settings.RefreshIntervalSeconds, 1, 3600) };
    }

    /// <summary>返回当前用户的配置文件路径，与 Codex 状态目录保持隔离。</summary>
    private static string GetDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "CodexQuotaWidget", "settings.json");
    }
}
