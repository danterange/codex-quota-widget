using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CodexQuotaWidget.Models;
using CodexQuotaWidget.Services;
using CodexQuotaWidget.ViewModels;

/// <summary>无第三方依赖的协议回归、桌面环境真实读取及 WPF 定时器检查。</summary>
internal static class Program
{
    /// <summary>同一测试程序可充当严格握手的子进程服务端，避免依赖远端数据做协议断言。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "app-server")
        {
            FakeServerAsync().GetAwaiter().GetResult();
            return 0;
        }
        try
        {
            if (args.Contains("--live"))
            {
                // 模拟 Explorer 的普通启动环境，不能借用 Codex 代理进程注入的路径。
                Environment.SetEnvironmentVariable("PATH",
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) + ";" +
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
                var result = new CodexQuotaProvider().GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check(result.FiveHour is not null || result.SevenDay is not null, "普通桌面环境返回真实额度");
                Console.WriteLine($"LIVE plan={result.PlanName} 5h={result.FiveHour?.Percent} weeklyRemaining={result.SevenDay?.Percent} resets={result.SevenDay?.ResetAt:O} resetCredits={result.ResetCredits}");
                CheckDispatcher(new CodexQuotaProvider(), live: true);
            }
            else
            {
                CheckLocator();
                CheckSettings();
                CheckProtocolAsync().GetAwaiter().GetResult();
                CheckDispatcher(new CountingProvider(), live: false);
            }
            Console.WriteLine("ALL CHECKS PASSED");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAILED: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    /// <summary>验证默认十秒、秒级范围校验和重启后的偏好持久化。</summary>
    private static void CheckSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "quota-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        Check(WidgetSettingsStore.Load(path).RefreshIntervalSeconds == 10, "默认刷新间隔为十秒");
        WidgetSettingsStore.Save(new WidgetSettings(25), path);
        Check(WidgetSettingsStore.Load(path).RefreshIntervalSeconds == 25, "刷新间隔可按秒保存");
        Check(WidgetSettingsStore.Normalize(new WidgetSettings(0)).RefreshIntervalSeconds == 1, "刷新间隔最小为一秒");
        Check(WidgetSettingsStore.Normalize(new WidgetSettings(5000)).RefreshIntervalSeconds == 3600, "刷新间隔最大为一小时");
    }

    /// <summary>构造 npm 存在但桌面原生程序位于散列目录的实际故障布局。</summary>
    private static void CheckLocator()
    {
        var root = Path.Combine(Path.GetTempPath(), "quota-checks-" + Guid.NewGuid().ToString("N"));
        var npm = Directory.CreateDirectory(Path.Combine(root, "npm")).FullName;
        var native = Directory.CreateDirectory(Path.Combine(root, "bin", "version-hash")).FullName;
        File.WriteAllText(Path.Combine(npm, "codex.cmd"), "@exit /b 1");
        File.WriteAllText(Path.Combine(native, "codex.exe"), "");
        Check(CodexExecutableLocator.Find(null, npm, Path.Combine(root, "bin")) == Path.Combine(native, "codex.exe"), "原生桌面程序优先于 npm 脚本");
        Check(CodexExecutableLocator.Find(null, npm, Path.Combine(root, "absent")) == Path.Combine(npm, "codex.cmd"), "无桌面版时仍能定位 npm");
        Check(CodexExecutableLocator.Find(null, root, Path.Combine(root, "absent")) is null, "未安装时返回缺失状态");
        Check(CodexExecutableLocator.Find(Path.Combine(root, "missing.exe"), npm, Path.Combine(root, "bin")) is null, "显式路径缺失时不偷偷切换");
    }

    /// <summary>从生产提供器发起真实子进程 RPC，验证握手、通知、错误、超时和清理。</summary>
    private static async Task CheckProtocolAsync()
    {
        var provider = new CodexQuotaProvider(Environment.ProcessPath);
        try
        {
            Environment.SetEnvironmentVariable("QUOTA_CHECK_MODE", "success");
            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);
            Check(snapshot.FiveHour is null && snapshot.SevenDay?.Percent == 45 && snapshot.ResetCredits == 2,
                "严格握手、跳过通知、仅周额度和重置次数");
            foreach (var item in new[] { ("broken", "Codex 安装不完整"), ("signed-out", "请先登录 Codex"), ("api-key", "需要 ChatGPT 登录"), ("unauthorized", "登录已失效"), ("timeout", "读取超时") })
            {
                Environment.SetEnvironmentVariable("QUOTA_CHECK_MODE", item.Item1);
                try
                {
                    await provider.GetSnapshotAsync(CancellationToken.None);
                    throw new Exception("应出现明确错误：" + item.Item1);
                }
                catch (QuotaReadException exception)
                {
                    Check(exception.Message == item.Item2, "错误分类 " + item.Item1);
                    Check(!exception.Detail.Contains("private-marker"), "不暴露服务端原始错误");
                }
            }
            Environment.SetEnvironmentVariable("QUOTA_CHECK_MODE", "timeout");
            using var cancellation = new CancellationTokenSource(200);
            try
            {
                await provider.GetSnapshotAsync(cancellation.Token);
                throw new Exception("取消未生效");
            }
            catch (OperationCanceledException) { Console.WriteLine("PASS 调用者取消"); }
        }
        finally { Environment.SetEnvironmentVariable("QUOTA_CHECK_MODE", null); }
    }

    /// <summary>用真实 WPF Dispatcher 验证两次十秒 Tick、错误恢复及关闭后的停止。</summary>
    private static void CheckDispatcher(IQuotaProvider provider, bool live)
    {
        var viewModel = new QuotaViewModel(provider);
        var frame = new DispatcherFrame();
        var updates = 0;
        Exception? failure = null;
        var started = DateTimeOffset.Now;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != string.Empty) return;
            if (viewModel.ErrorText.Length == 0)
            {
                updates++;
                Console.WriteLine($"REFRESH success={updates} elapsed={(DateTimeOffset.Now - started).TotalSeconds:0.0}s");
            }
        };
        var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            try
            {
                Check(viewModel.ErrorText.Length == 0, "最后一次刷新无错误");
                Check(viewModel.SevenDayVisibility == Visibility.Visible, "真实绑定显示周额度");
                Check(updates >= 2, "观察到两次自动成功刷新");
                if (!live) Check(((CountingProvider)provider).Calls == 3, "启动和两次十秒刷新恰好三次调用");
                viewModel.Dispose();
                if (!live)
                {
                    viewModel.RefreshNowAsync().GetAwaiter().GetResult();
                    Check(((CountingProvider)provider).Calls == 3, "关闭后不再读取");
                }
            }
            catch (Exception exception) { failure = exception; }
            finally { viewModel.Dispose(); frame.Continue = false; }
        };
        stop.Start();
        Dispatcher.PushFrame(frame);
        if (failure is not null) throw failure;
    }

    /// <summary>只有 initialize 响应和 initialized 通知均完成后才接受账号读取。</summary>
    private static async Task FakeServerAsync()
    {
        var mode = Environment.GetEnvironmentVariable("QUOTA_CHECK_MODE");
        if (mode == "broken")
        {
            await Console.Error.WriteLineAsync("Missing optional dependency @openai/codex-win32-x64 private-marker");
            return;
        }
        if (mode == "timeout") { await Task.Delay(30000); return; }
        var initialized = false;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var method = document.RootElement.GetProperty("method").GetString();
            if (method == "initialize") await Console.Out.WriteLineAsync("""{"id":1,"result":{}}""");
            else if (method == "initialized") initialized = true;
            else if (!initialized) await Console.Out.WriteLineAsync("""{"id":2,"error":{"message":"Not initialized"}}""");
            else if (method == "account/read")
            {
                var account = mode == "signed-out" ? "null" : mode == "api-key" ? """{"type":"apiKey"}""" : """{"type":"chatgpt","planType":"pro"}""";
                await Console.Out.WriteLineAsync("""{"method":"notification","params":{}}""");
                await Console.Out.WriteLineAsync("{\"id\":2,\"result\":{\"account\":" + account + "}}");
            }
            else if (method == "account/rateLimits/read")
            {
                await Console.Out.WriteLineAsync(mode == "unauthorized"
                    ? """{"id":3,"error":{"message":"401 unauthorized private-marker"}}"""
                    : """{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":55,"windowDurationMins":10080,"resetsAt":1893456000},"secondary":null},"rateLimitResetCredits":{"availableCount":2}}}""");
            }
            await Console.Out.FlushAsync();
        }
    }

    /// <summary>失败立即终止验收，防止把程序存活误判为读取成功。</summary>
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        Console.WriteLine("PASS " + description);
    }

    /// <summary>第一次失败、随后恢复，用于验证 UI 不会永久停留在错误态。</summary>
    private sealed class CountingProvider : IQuotaProvider
    {
        public int Calls { get; private set; }

        /// <summary>记录实际请求数，同时构造恢复后的周窗口。</summary>
        public Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1) throw new QuotaReadException("测试失败", "测试错误恢复");
            return Task.FromResult(new QuotaSnapshot("test", "Pro", null, new QuotaWindow("7d", 45, DateTimeOffset.Now.AddDays(1)), null, null));
        }
    }
}
