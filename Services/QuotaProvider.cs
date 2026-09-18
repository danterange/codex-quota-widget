using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CodexQuotaWidget.Models;

namespace CodexQuotaWidget.Services;

/// <summary>为界面提供当前账号额度快照；实现必须保持只读，不执行账号变更操作。</summary>
public interface IQuotaProvider
{
    /// <summary>异步读取当前账号的额度和会员信息，支持调用方取消等待。</summary>
    Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>提供本地演示数据，使组件在没有 Codex 登录状态时仍可进行 UI 验证。</summary>
public sealed class DemoQuotaProvider : IQuotaProvider
{
    /// <summary>返回固定额度示例；重置时间相对当前时刻计算，便于观察倒计时。</summary>
    public Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        return Task.FromResult(new QuotaSnapshot("demo", "Pro", new QuotaWindow("5h", 42, now.AddHours(1).AddMinutes(26)), new QuotaWindow("7d", 68, now.AddDays(4).AddHours(8)), new DateTimeOffset(now.Year, 10, 12, 23, 59, 0, now.Offset), 1) { AccountKey = "demo" });
    }
}

/// <summary>通过本机 Codex app-server 的 JSON-RPC 只读接口读取当前身份和额度。</summary>
public sealed class CodexQuotaProvider : IQuotaProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private readonly string? executablePath;

    /// <summary>初始化提供器；未传路径时按 PATH 和桌面打包路径依次探查。</summary>
    public CodexQuotaProvider(string? codexExecutablePath = null) => executablePath = ResolveExecutable(codexExecutablePath);

    /// <summary>启动短生命周期 app-server 会话，读取两个响应后退出。</summary>
    public async Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var path = executablePath ?? throw new InvalidOperationException("Codex CLI executable was not found.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var process = new Process { StartInfo = CreateStartInfo(path), EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Codex app-server could not be started.");
        try
        {
            await SendAsync(process, "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-quota-widget\",\"version\":\"0.1.0\"}}}", timeout.Token);
            await SendAsync(process, "{\"id\":2,\"method\":\"account/read\",\"params\":{}}", timeout.Token);
            await SendAsync(process, "{\"id\":3,\"method\":\"account/rateLimits/read\",\"params\":{}}", timeout.Token);
            JsonElement? account = null;
            JsonElement? limits = null;
            while (account is null || limits is null)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("Codex app-server closed before returning account data.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                var idValue = id.GetInt32();
                if (idValue == 2 && root.TryGetProperty("result", out var accountResult)) account = accountResult.Clone();
                else if (idValue == 3 && root.TryGetProperty("result", out var limitsResult)) limits = limitsResult.Clone();
                else if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex app-server rejected a read request.");
            }
            return ParseSnapshot(account.Value, limits.Value);
        }
        finally { TryTerminate(process); }
    }

    /// <summary>将协议响应转换为领域快照；窗口按分钟数分类，避免周限额误标为 5 小时。</summary>
    private static QuotaSnapshot ParseSnapshot(JsonElement accountResponse, JsonElement limitsResponse)
    {
        var account = accountResponse.TryGetProperty("account", out var accountValue) ? accountValue : default;
        var email = account.ValueKind == JsonValueKind.Object && account.TryGetProperty("email", out var emailValue) ? emailValue.GetString() : null;
        var accountId = limitsResponse.TryGetProperty("accountId", out var idValue) ? idValue.GetString() : null;
        var accountLabel = !string.IsNullOrWhiteSpace(email) ? MaskEmail(email!) : accountId ?? "account";
        var plan = account.ValueKind == JsonValueKind.Object && account.TryGetProperty("planType", out var planValue) ? planValue.GetString() ?? "unknown" : "unknown";
        var windows = new List<QuotaWindow>();
        AddWindows(limitsResponse, windows);
        var resetCredits = limitsResponse.TryGetProperty("rateLimitResetCredits", out var credits) && credits.ValueKind == JsonValueKind.Object && credits.TryGetProperty("availableCount", out var count) ? count.GetInt32() : (int?)null;
        var membershipExpiresAt = FindMembershipExpiry(account);
        return new QuotaSnapshot(accountLabel, plan, windows.Find(window => window.Label == "5h"), windows.Find(window => window.Label == "7d"), membershipExpiresAt, resetCredits) { AccountKey = accountId ?? accountLabel };
    }

    /// <summary>收集单桶或多桶响应中的非空窗口。</summary>
    private static void AddWindows(JsonElement response, ICollection<QuotaWindow> windows)
    {
        if (response.TryGetProperty("rateLimitsByLimitId", out var byLimitId) && byLimitId.ValueKind == JsonValueKind.Object)
            foreach (var item in byLimitId.EnumerateObject()) AddWindowsFromSnapshot(item.Value, windows);
        else if (response.TryGetProperty("rateLimits", out var single)) AddWindowsFromSnapshot(single, windows);
    }

    /// <summary>兼容不同 app-server 版本可能使用的会员到期字段。</summary>
    private static DateTimeOffset? FindMembershipExpiry(JsonElement account)
    {
        if (account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "membershipExpiresAt", "subscriptionExpiresAt", "planExpiresAt", "expiresAt" })
        {
            if (!account.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixSeconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
            }

            if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            {
                return parsed.ToLocalTime();
            }
        }

        return null;
    }

    /// <summary>根据 primary/secondary 的分钟数确定标签；未知窗口不伪造为支持的额度。</summary>
    private static void AddWindowsFromSnapshot(JsonElement snapshot, ICollection<QuotaWindow> windows)
    {
        foreach (var propertyName in new[] { "primary", "secondary" })
        {
            if (!snapshot.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("usedPercent", out var used)) continue;
            var duration = window.TryGetProperty("windowDurationMins", out var durationValue) && durationValue.ValueKind == JsonValueKind.Number ? durationValue.GetInt64() : 0;
            var label = duration switch { 300 => "5h", 10080 => "7d", _ => null };
            if (label is null) continue;
            DateTimeOffset? resetAt = null;
            if (window.TryGetProperty("resetsAt", out var resetValue) && resetValue.ValueKind == JsonValueKind.Number) resetAt = DateTimeOffset.FromUnixTimeSeconds(resetValue.GetInt64()).ToLocalTime();
            windows.Add(new QuotaWindow(label, Math.Clamp(used.GetDouble(), 0, 100), resetAt));
        }
    }

    /// <summary>写入一行 JSON-RPC 请求，使用取消令牌避免永久阻塞。</summary>
    private static async Task SendAsync(Process process, string payload, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    /// <summary>掩码邮箱本地部分，避免快照意外暴露完整身份。</summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "account" : $"{email[..1]}***{email[at..]}";
    }

    /// <summary>查找本机 CLI 路径，不读取或输出任何凭据。</summary>
    private static string? ResolveExecutable(string? configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath);
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(path => Path.Combine(path, "codex.exe")));
        candidates.AddRange(pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(path => Path.Combine(path, "codex.cmd")));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin", "codex.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 为可执行文件或 npm 的 cmd 启动脚本构造同样支持标准输入输出的进程配置。
    /// </summary>
    private static ProcessStartInfo CreateStartInfo(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"{path}\" app-server --stdio");
        }
        else
        {
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
        }

        return startInfo;
    }

    /// <summary>终止临时会话并吞掉清理阶段错误，避免覆盖真实读取结果。</summary>
    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
