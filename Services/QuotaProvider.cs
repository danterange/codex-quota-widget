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
    private readonly string? configuredPath;

    /// <summary>初始化提供器；未传路径时按 PATH 和桌面打包路径依次探查。</summary>
    public CodexQuotaProvider(string? codexExecutablePath = null) => configuredPath = codexExecutablePath;

    /// <summary>启动短生命周期 app-server 会话，完成握手并读取账号与额度响应后退出。</summary>
    public async Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var path = CodexExecutableLocator.Find(configuredPath)
            ?? throw new QuotaReadException("未找到 Codex", "请安装并登录 Codex 桌面应用或 CLI，再刷新额度。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var process = new Process { StartInfo = CreateStartInfo(path), EnableRaisingEvents = true };
        if (!process.Start()) throw new QuotaReadException("Codex 启动失败", "请检查 Codex 安装是否完整。");
        // 持续排空 stderr，避免日志管道填满导致 stdout 的额度响应停滞。
        var stderr = DrainErrorsAsync(process.StandardError, timeout.Token);
        try
        {
            await SendAsync(process, "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-quota-widget\",\"version\":\"0.1.0\"}}}", timeout.Token);
            await ReadResponseAsync(process, 1, stderr, timeout.Token);
            await SendAsync(process, "{\"method\":\"initialized\",\"params\":{}}", timeout.Token);
            await SendAsync(process, "{\"id\":2,\"method\":\"account/read\",\"params\":{}}", timeout.Token);
            var account = await ReadResponseAsync(process, 2, stderr, timeout.Token);
            if (!account.TryGetProperty("account", out var identity) || identity.ValueKind == JsonValueKind.Null)
            {
                throw new QuotaReadException("请先登录 Codex", "请在 Codex 中登录 ChatGPT 账号，组件将在下一次刷新时重试。");
            }
            if (identity.TryGetProperty("type", out var type) && type.GetString() != "chatgpt")
            {
                throw new QuotaReadException("需要 ChatGPT 登录", "API Key 登录不提供 Plus / Pro 的订阅额度。");
            }
            await SendAsync(process, "{\"id\":3,\"method\":\"account/rateLimits/read\",\"params\":{}}", timeout.Token);
            var limits = await ReadResponseAsync(process, 3, stderr, timeout.Token);
            var snapshot = ParseSnapshot(account, limits);
            if (snapshot.FiveHour is null && snapshot.SevenDay is null)
            {
                throw new QuotaReadException("暂无额度数据", "Codex 未返回 5 小时或 7 天窗口，组件将自动重试。");
            }
            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QuotaReadException("读取超时", "8 秒内未收到额度响应，请检查网络；组件会自动重试。");
        }
        finally
        {
            TryTerminate(process);
            await timeout.CancelAsync();
            await stderr;
        }
    }

    /// <summary>等待指定响应并跳过通知；原始错误只用于分类，绝不直接回显。</summary>
    private static async Task<JsonElement> ReadResponseAsync(Process process, int requestId, Task<bool> stderr, CancellationToken token)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null)
            {
                if (await WaitForErrorStreamAsync(process, stderr, token))
                {
                    throw new QuotaReadException("Codex 安装不完整", "npm 版缺少平台依赖；可安装并登录 Codex 桌面版，或修复 CLI 安装。");
                }
                throw new QuotaReadException("Codex 启动失败", "Codex 在返回额度前退出，请检查安装和本机配置。");
            }
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value) || value != requestId)
            {
                continue;
            }
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() ?? "" : "";
                if (message.Contains("401") || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    throw new QuotaReadException("登录已失效", "请在 Codex 中重新登录 ChatGPT 账号，然后刷新额度。");
                }
                throw new QuotaReadException("额度接口请求失败", "Codex 未能获取额度，请检查网络和登录状态；组件会自动重试。");
            }
            return root.GetProperty("result").Clone();
        }
    }

    /// <summary>处理 stdout 先关闭而 stderr 尚未完全结束的进程竞态，避免误报取消。</summary>
    private static async Task<bool> WaitForErrorStreamAsync(Process process, Task<bool> stderr, CancellationToken token)
    {
        try
        {
            return process.HasExited
                ? await stderr.WaitAsync(TimeSpan.FromSeconds(1))
                : await stderr.WaitAsync(token);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>丢弃原始诊断内容，仅保留缺少 npm 依赖的布尔标记，限制内存和信息暴露。</summary>
    private static async Task<bool> DrainErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var missingDependency = false;
        try
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                missingDependency |= line.Contains("Missing optional dependency", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return missingDependency;
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

    /// <summary>
    /// 探测 app-server 可能返回的会员到期字段；只有名称明确指向会员或订阅时才使用，绝不把额度窗口或重置券到期误标为会员时间。
    /// </summary>
    private static DateTimeOffset? FindMembershipExpiry(JsonElement account)
    {
        if (account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "membershipExpiresAt", "subscriptionExpiresAt", "planExpiresAt" })
        {
            if (!account.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixTimestamp))
            {
                return ParseUnixTimestamp(unixTimestamp);
            }

            if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            {
                return parsed.ToLocalTime();
            }
        }

        return null;
    }

    /// <summary>兼容服务端以秒或毫秒表示的 Unix 时间，统一转换为当前用户本地时间。</summary>
    private static DateTimeOffset? ParseUnixTimestamp(long unixTimestamp)
    {
        try
        {
            return Math.Abs(unixTimestamp) >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixTimestamp).ToLocalTime()
                : DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
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
            var remainingPercent = 100 - Math.Clamp(used.GetDouble(), 0, 100);
            windows.Add(new QuotaWindow(label, remainingPercent, resetAt));
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
            // cmd 的 /c 有独立的引号规则，不能把整条命令交给 ArgumentList 做 CRT 转义。
            startInfo.Arguments = $"/d /s /c \"\"{path}\" app-server\"";
        }
        else
        {
            startInfo.ArgumentList.Add("app-server");
        }

        return startInfo;
    }

    /// <summary>终止临时会话并吞掉清理阶段错误，避免覆盖真实读取结果。</summary>
    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
