using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace CodexQuotaWidget.Services;

/// <summary>
/// 启动随小组件发布的 PowerShell 7 重启助手；该类只编排助手和归纳非敏感结果，绝不按进程名关闭 Codex 或 CLI。
/// </summary>
internal sealed class CodexDesktopRestartService
{
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustUnionChoiceFile = 1;
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(130);
    private static readonly JsonSerializerOptions HelperJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex RunIdPattern = new("^\\d{8}-\\d{6}-[a-f0-9]{8}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly string? testHelperScriptPath;
    private readonly string? testPowerShellPath;

    /// <summary>创建发布版服务；可选路径仅供同程序集回归测试注入，主窗口从不读取环境变量来替换隐藏执行入口。</summary>
    internal CodexDesktopRestartService(string? helperScriptPath = null, string? powerShellPath = null)
    {
        testHelperScriptPath = helperScriptPath;
        testPowerShellPath = powerShellPath;
    }

    /// <summary>调用桌面端专用重启助手并解析其最终状态；强制结束仅能由界面的第二次明确确认传入，默认失败时保持当前 Codex 进程不变。</summary>
    internal async Task<CodexDesktopRestartResult> RestartAsync(CancellationToken cancellationToken, bool forceAfterTimeout = false)
    {
        var scriptPath = FindHelperScriptPath(testHelperScriptPath);
        if (scriptPath is null)
        {
            return CodexDesktopRestartResult.Unavailable;
        }

        var shell = FindPowerShellPath(testPowerShellPath);
        if (shell is null)
        {
            return CodexDesktopRestartResult.Unavailable;
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(shell, scriptPath, forceAfterTimeout),
            EnableRaisingEvents = true
        };
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            if (!process.Start())
            {
                return CodexDesktopRestartResult.Unavailable;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitForExit = process.WaitForExitAsync(cancellationToken);
            var timeout = Task.Delay(HelperTimeout, cancellationToken);
            var completed = await Task.WhenAny(waitForExit, timeout);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != waitForExit)
            {
                // 不在这里杀死助手：它可能已将可信的桌面进程树交给独立监督器，强行中断会留下未知状态。
                return CodexDesktopRestartResult.TimedOut;
            }

            await waitForExit;
            var output = await stdout;
            _ = await stderr; // 助手只把敏感诊断保留在本地报告，不在小组件界面回显。
            return await ParseResultAsync(process.ExitCode, output, startedAt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CodexDesktopRestartResult.Cancelled;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            return CodexDesktopRestartResult.Unavailable;
        }
    }

    /// <summary>构造固定参数的隐藏 PowerShell 7 子进程；仅 <c>Desktop</c> 模式可关闭 GUI，强制参数必须由调用方的二次确认显式传入。</summary>
    private static ProcessStartInfo CreateStartInfo(string shell, string scriptPath, bool forceAfterTimeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Mode");
        startInfo.ArgumentList.Add("Desktop");
        startInfo.ArgumentList.Add("-Execute");
        if (forceAfterTimeout)
        {
            startInfo.ArgumentList.Add("-ForceAfterTimeout");
        }
        return startInfo;
    }

    /// <summary>解析助手的调度结果，并在获得报告路径后等待监督器的最终状态，避免把“已启动监督器”误报为重启完成。</summary>
    private static async Task<CodexDesktopRestartResult> ParseResultAsync(int exitCode, string output, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        const string prefix = "[RESULT] ";
        var resultLine = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(resultLine))
        {
            return exitCode == 0 ? CodexDesktopRestartResult.Unavailable : CodexDesktopRestartResult.Failed;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<RestartHelperPayload>(resultLine[prefix.Length..], HelperJsonOptions);
            if (payload?.State == "desktop_restart_scheduled" && !string.IsNullOrWhiteSpace(payload.ReportPath) && !string.IsNullOrWhiteSpace(payload.RunId))
            {
                return await WaitForSupervisorResultAsync(payload.ReportPath, payload.RunId, startedAt, cancellationToken);
            }

            return MapState(payload?.State, exitCode);
        }
        catch (JsonException)
        {
            return CodexDesktopRestartResult.Failed;
        }
    }

    /// <summary>在有限时间内读取监督器原子写入的最终报告；只使用状态字段，不将会话候选或本机路径显示在小组件上。</summary>
    private static async Task<CodexDesktopRestartResult> WaitForSupervisorResultAsync(string reportPath, string runId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        if (!IsExpectedReportPath(reportPath, runId))
        {
            return CodexDesktopRestartResult.Failed;
        }

        var deadline = DateTimeOffset.UtcNow.Add(HelperTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(reportPath))
                {
                    var info = new FileInfo(reportPath);
                    if (info.LastWriteTimeUtc < startedAt.UtcDateTime.AddSeconds(-2))
                    {
                        return CodexDesktopRestartResult.Failed;
                    }

                    var report = JsonSerializer.Deserialize<RestartSupervisorReport>(await File.ReadAllTextAsync(reportPath, cancellationToken), HelperJsonOptions);
                    if (report is null || !string.Equals(report.RunId, runId, StringComparison.Ordinal))
                    {
                        return CodexDesktopRestartResult.Failed;
                    }

                    if (!string.IsNullOrWhiteSpace(report.State))
                    {
                        return MapState(report.State, exitCode: 1);
                    }
                }
            }
            catch (IOException)
            {
                // 监督器以原子替换写报告；短暂竞争时等待下一轮而不是将正在写入误判为失败。
            }
            catch (JsonException)
            {
                // 原子替换前的文件可能来自上一次异常尝试；继续等候当前监督器的最终报告。
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return CodexDesktopRestartResult.Failed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        return CodexDesktopRestartResult.TimedOut;
    }

    /// <summary>验证报告只能来自本次脚本生成的用户本地 run 目录，阻止任意绝对路径或旧报告被用来伪造“已重启”。</summary>
    private static bool IsExpectedReportPath(string reportPath, string runId)
    {
        if (!Path.IsPathFullyQualified(reportPath) || !RunIdPattern.IsMatch(runId))
        {
            return false;
        }

        try
        {
            var runRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexDesktopRestart",
                "runs"));
            var expected = Path.GetFullPath(Path.Combine(runRoot, runId, "restart-report.json"));
            return string.Equals(Path.GetFullPath(reportPath), expected, StringComparison.OrdinalIgnoreCase)
                && expected.StartsWith(runRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>将脚本和监督器状态归并为受限枚举；未列出的状态永远不会被 UI 当成成功。</summary>
    private static CodexDesktopRestartResult MapState(string? state, int exitCode)
    {
        return state switch
        {
            "desktop_relaunched_manifest_ready" => CodexDesktopRestartResult.Relaunched,
            "desktop_relaunched_login_user_action_required" => CodexDesktopRestartResult.LoginRequired,
            "desktop_close_timeout_needs_force_confirmation" => CodexDesktopRestartResult.NeedsManualClose,
            "desktop_restart_not_ready" or "restart_rejected" => CodexDesktopRestartResult.Unavailable,
            "desktop_launch_timeout" or "desktop_relaunched_health_unverified" or "failed" => CodexDesktopRestartResult.Failed,
            _ => exitCode == 0 ? CodexDesktopRestartResult.Unavailable : CodexDesktopRestartResult.Failed
        };
    }

    /// <summary>优先使用程序集内测试注入的脚本；发布路径始终固定在应用目录，避免继承环境变量把隐藏点击重定向到任意脚本。</summary>
    private static string? FindHelperScriptPath(string? injectedTestPath)
    {
        if (!string.IsNullOrWhiteSpace(injectedTestPath))
        {
            return Path.IsPathFullyQualified(injectedTestPath) && File.Exists(injectedTestPath) ? injectedTestPath : null;
        }

        var installed = Path.Combine(AppContext.BaseDirectory, "restart", "Restart-CodexDesktop.ps1");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>查找经 Microsoft 签名的 PowerShell 7；测试可注入显式路径，发布版只接受受信任候选且绝不回退到 Windows PowerShell 5.1。</summary>
    private static string? FindPowerShellPath(string? injectedTestPath)
    {
        if (!string.IsNullOrWhiteSpace(injectedTestPath))
        {
            return Path.IsPathFullyQualified(injectedTestPath) && File.Exists(injectedTestPath) ? injectedTestPath : null;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (IsTrustedPowerShell(installed))
        {
            return installed;
        }

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim().Trim('"'))
            .Where(Path.IsPathFullyQualified);
        return pathEntries.Select(entry => Path.Combine(entry, "pwsh.exe")).FirstOrDefault(IsTrustedPowerShell);
    }

    /// <summary>检查候选是否为 Microsoft 签名的 PowerShell 可执行文件，减少 PATH 被篡改时将任意程序作为隐藏重启助手运行的风险。</summary>
    private static bool IsTrustedPowerShell(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetFileName(path), "pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return string.Equals(version.CompanyName, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                && version.ProductName?.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) == true
                && certificate.Subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                && HasTrustedAuthenticodeSignature(path);
        }
        catch (Exception exception) when (exception is CryptographicException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>通过 Windows WinVerifyTrust 验证签名链，而非仅信任可伪造的文件版本字段或证书 Subject。</summary>
    private static bool HasTrustedAuthenticodeSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint) Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path
        };
        var fileInfoPointer = IntPtr.Zero;
        var trustDataPointer = IntPtr.Zero;
        try
        {
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint) Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustUiNone,
                UnionChoice = WinTrustUnionChoiceFile,
                FileInfoPointer = fileInfoPointer
            };
            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, false);
            var action = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(trustDataPointer);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }
    }

    /// <summary>承载助手输出的最小公开字段；路径和诊断不会进入 UI，避免将本机会话细节暴露给悬浮小组件。</summary>
    private sealed record RestartHelperPayload(string? State, string? ReportPath = null, string? RunId = null);

    /// <summary>承载监督器落盘报告的唯一关联字段；报告必须携带与启动结果相同的 run_id 才能改变用户可见状态。</summary>
    private sealed record RestartSupervisorReport(
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("run_id")] string? RunId);

    /// <summary>WinVerifyTrust 的文件输入结构；只传入目标文件路径，不提供网络 URL、状态句柄或用户界面回调。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] internal string FilePath;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubjectPointer;
    }

    /// <summary>WinVerifyTrust 的最小数据结构；使用无 UI、文件联合选项，仅验证签名链。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfoPointer;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }

    /// <summary>调用 Windows 信任服务验证 Authenticode；返回零才表示链验证成功。</summary>
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern uint WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, IntPtr trustData);
}

/// <summary>归纳桌面端重启助手的安全结果，调用方只能据此显示提示，不能从状态推断未完成会话已自动恢复。</summary>
internal enum CodexDesktopRestartResult
{
    /// <summary>重启助手已确认重新打开桌面端并准备好恢复清单，未完成线程仍须用户手动继续。</summary>
    Relaunched,

    /// <summary>桌面端已重新打开但缓存登录不能复用，用户需要在桌面端完成登录。</summary>
    LoginRequired,

    /// <summary>正常关闭超时且未获得额外强制结束授权，现有桌面端保持运行。</summary>
    NeedsManualClose,

    /// <summary>无法安全取得仅含元数据的会话快照，因此助手拒绝关闭桌面端。</summary>
    SnapshotUnavailable,

    /// <summary>助手或 PowerShell 7 不可用、未找到桌面端，或当前环境不支持安全重启。</summary>
    Unavailable,

    /// <summary>助手等待过久；为避免中断独立监督器，小组件未追加终止操作。</summary>
    TimedOut,

    /// <summary>窗口关闭或调用取消使等待停止，原桌面端状态由助手报告决定。</summary>
    Cancelled,

    /// <summary>助手返回未识别或失败结果，不能将其展示为成功。</summary>
    Failed
}
