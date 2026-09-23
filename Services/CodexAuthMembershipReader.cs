using System.Text;
using System.Text.Json;
using System.IO;
using System.Globalization;

namespace CodexQuotaWidget.Services;

/// <summary>
/// 从 Codex 本地登录令牌读取订阅有效期。令牌只在进程内解析，永不写入日志或 UI。
/// </summary>
internal static class CodexAuthMembershipReader
{
    private const string AuthClaim = "https://api.openai.com/auth";
    private const string ExpiryClaim = "chatgpt_subscription_active_until";

    /// <summary>读取当前 Codex 登录状态中的订阅有效期；凭据缺失或格式变化时返回空值。</summary>
    internal static DateTimeOffset? Read()
    {
        try
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }

            return ReadFromFile(Path.Combine(codexHome, "auth.json"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>读取指定凭据文件，供桌面运行和离线回归测试共同复用。</summary>
    internal static DateTimeOffset? ReadFromFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var tokenName in new[] { "id_token", "access_token" })
            {
                if (!tokens.TryGetProperty(tokenName, out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var expiry = ReadTokenExpiry(tokenValue.GetString());
                if (expiry is not null)
                {
                    return expiry;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // 登录文件是可选数据；额度协议本身仍可正常显示。
        }

        return null;
    }

    /// <summary>解析 JWT 的非敏感 payload，并只接受明确的 ChatGPT 订阅有效期声明。</summary>
    private static DateTimeOffset? ReadTokenExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(AuthClaim, out var auth) || auth.ValueKind != JsonValueKind.Object
                || !auth.TryGetProperty(ExpiryClaim, out var expiry) || expiry.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime()
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
