using Microsoft.Win32;
using System.IO;

namespace CodexQuotaWidget.Services;

/// <summary>描述一次当前用户开机启动设置操作的结果，供设置页显示可操作的反馈。</summary>
internal sealed record AutoStartResult(bool IsAvailable, bool Succeeded, string? Detail);

/// <summary>维护当前用户的 Windows Run 项，不接触管理员级启动项或其他应用的注册表值。</summary>
internal static class AutoStartService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "CodexQuotaWidget";

    /// <summary>
    /// 根据偏好写入或移除本应用的 HKCU Run 值；开发目录的 apphost 会变化，因此不会被注册为开机启动。
    /// </summary>
    internal static AutoStartResult Apply(bool enabled, string? executablePath = null)
    {
        var command = CreateCommand(executablePath ?? Environment.ProcessPath);
        if (command is null)
        {
            return new AutoStartResult(false, false, null);
        }

        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                runKey.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return new AutoStartResult(true, true, null);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new AutoStartResult(true, false, exception.Message);
        }
    }

    /// <summary>
    /// 仅为安装版或便携版生成带引号的启动命令，避免调试 `bin` 目录被清理后留下失效启动项。
    /// </summary>
    internal static string? CreateCommand(string? executablePath)
    {
        var path = executablePath;
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !path.EndsWith("CodexQuotaWidget.exe", StringComparison.OrdinalIgnoreCase)
            || path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"\"{path}\"";
    }
}
