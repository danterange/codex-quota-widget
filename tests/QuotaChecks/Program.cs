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
            if (args.Contains("--window-shell"))
            {
                if (!args.Contains("--demo")) throw new ArgumentException("Window shell checks require --demo.");
                CheckWindowShell();
                return 0;
            }
            if (args.Contains("--live"))
            {
                // 模拟 Explorer 的普通启动环境，不能借用 Codex 代理进程注入的路径。
                Environment.SetEnvironmentVariable("PATH",
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) + ";" +
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
                var result = new CodexQuotaProvider().GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check(result.FiveHour is not null || result.SevenDay is not null, "普通桌面环境返回真实额度");
                Check(result.MembershipExpiresAt is not null, "普通桌面环境返回自动会员到期时间");
                Console.WriteLine($"LIVE plan={result.PlanName} 5h={result.FiveHour?.Percent} weeklyRemaining={result.SevenDay?.Percent} resets={result.SevenDay?.ResetAt:O} membership={result.MembershipExpiresAt:O} resetCredits={result.ResetCredits}");
                CheckDispatcher(new CodexQuotaProvider(), live: true);
            }
            else
            {
                CheckLocator();
                CheckSettings();
                CheckMembershipPresentation();
                CheckStaleSnapshotAsync().GetAwaiter().GetResult();
                CheckProtocolAsync().GetAwaiter().GetResult();
                CheckDispatcher(new CountingProvider(), live: false);
            }
            Console.WriteLine("ALL CHECKS PASSED");
            return 0;
        }
        catch (Exception exception)
        {
            if (args.Contains("--window-shell")) Console.Error.WriteLine(exception.StackTrace);
            Console.Error.WriteLine($"FAILED: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    /// <summary>使用隔离窗口验证英文布局、任务栏排除和隐藏恢复，不创建托盘、不写入用户设置。</summary>
    private static void CheckWindowShell()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(Directory.GetCurrentDirectory(), "App.xaml"));
        System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new System.Xml.Linq.XElement(presentation + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            document.Root!.Element(presentation + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        var window = new CodexQuotaWidget.MainWindow(new WidgetSettings(Language: AppLanguage.English), _ => { });
        try
        {
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Check(window.ActualWidth == 320 && window.ActualHeight <= 280, "英文主界面保持紧凑尺寸");
            CheckTaskbarExcluded(window, "首次显示");
            var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui-test");
            Directory.CreateDirectory(output);
            SaveWindowRender(window, Path.Combine(output, "english-dashboard.png"));
            var settings = (System.Windows.Controls.RadioButton)window.FindName("SettingsRailButton");
            settings.IsChecked = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            SaveWindowRender(window, Path.Combine(output, "english-settings.png"));
            CheckTaskbarExcluded(window, "设置页");
            window.WindowState = WindowState.Maximized;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            CheckTaskbarExcluded(window, "最大化");
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            CheckTaskbarExcluded(window, "还原");
            window.Close();
            Check(!window.IsVisible && !window.Dispatcher.HasShutdownStarted, "正常关闭仅隐藏主窗口");
            window.Show();
            Check(window.IsVisible, "隐藏后同一窗口可以恢复");
            CheckTaskbarExcluded(window, "隐藏后恢复");
            Console.WriteLine("WINDOW SHELL CHECKS PASSED");
        }
        finally
        {
            window.RequestApplicationExit();
            app.Shutdown();
        }
    }

    /// <summary>同时验证 WPF 配置与原生窗口状态，防止窗口还原时重新注册任务栏入口。</summary>
    private static void CheckTaskbarExcluded(Window window, string phase)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        const int extendedStyleIndex = -20;
        const long appWindowStyle = 0x00040000;
        const uint ownerRelationship = 4;
        var extendedStyle = GetWindowLongPtr(handle, extendedStyleIndex).ToInt64();
        // WPF 在 ShowInTaskbar=false 时提供隐藏 owner，阻止 Shell 把主窗口视作独立任务栏项。
        Check(!window.ShowInTaskbar && (extendedStyle & appWindowStyle) == 0
              && GetWindow(handle, ownerRelationship) != IntPtr.Zero, phase + "不创建任务栏入口");
    }

    /// <summary>只读取得扩展窗口样式，检查 WS_EX_APPWINDOW 没有被恢复操作打开。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    /// <summary>只读取得 WPF 隐藏 owner，不枚举或改变其他程序的窗口。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint relationship);

    /// <summary>保存隔离窗口的真实 WPF 渲染，供人工复核文本换行和控件裁切。</summary>
    private static void SaveWindowRender(Window window, string path)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>验证默认偏好、语言持久化和刷新范围校验。</summary>
    private static void CheckSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "quota-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        var defaults = WidgetSettingsStore.Load(path);
        Check(defaults.RefreshIntervalSeconds == 10 && defaults.Language == AppLanguage.SimplifiedChinese,
            "默认刷新间隔和中文偏好正确");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"RefreshIntervalSeconds\":30}");
        var upgraded = WidgetSettingsStore.Load(path);
        Check(upgraded.RefreshIntervalSeconds == 30 && upgraded.Language == AppLanguage.SimplifiedChinese,
            "旧版仅有刷新间隔的配置会升级为中文");
        WidgetSettingsStore.Save(new WidgetSettings(25, AppLanguage.English), path);
        var saved = WidgetSettingsStore.Load(path);
        Check(saved.RefreshIntervalSeconds == 25 && saved.Language == AppLanguage.English, "设置页偏好可完整保存");
        Check(WidgetSettingsStore.Normalize(new WidgetSettings(0)).RefreshIntervalSeconds == 1, "刷新间隔最小为一秒");
                Check(WidgetSettingsStore.Normalize(new WidgetSettings(5000)).RefreshIntervalSeconds == 3600, "刷新间隔最大为一小时");
        CheckAuthMembershipReader();
    }

    /// <summary>复现先成功后连续失败的刷新链，确保旧快照保留且第三次失败前不显示异常。</summary>
    private static async Task CheckStaleSnapshotAsync()
    {
        var provider = new StaleSnapshotProvider();
        var viewModel = new QuotaViewModel(provider, refreshIntervalSeconds: 3600);
        try
        {
            while (provider.Calls < 1) await Task.Delay(5);
            await viewModel.RefreshNowAsync();
            Check(viewModel.SevenDayPercent == 55 && viewModel.ConnectionText == "正在获取最新数据…", "一次失败保留旧额度并显示获取最新数据");
            await viewModel.RefreshNowAsync();
            Check(viewModel.SevenDayPercent == 55 && viewModel.ConnectionText == "连接异常", "连续两次失败才显示连接异常");
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    /// <summary>验证从本地 JWT 的 OpenAI 认证声明读取订阅有效期，不把令牌本身暴露到测试输出。</summary>
    private static void CheckAuthMembershipReader()
    {
        var path = Path.Combine(Path.GetTempPath(), "quota-auth-" + Guid.NewGuid().ToString("N") + ".json");
        const string payload = "{\"https://api.openai.com/auth\":{\"chatgpt_subscription_active_until\":\"2026-10-10T17:42:32+08:00\"}}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(path, "{\"tokens\":{\"id_token\":\"header." + encoded + ".signature\"}}");
        try
        {
            Check(CodexAuthMembershipReader.ReadFromFile(path) == new DateTimeOffset(2026, 10, 10, 17, 42, 32, TimeSpan.FromHours(8)).ToLocalTime(),
                "本地登录令牌可读取会员到期时间");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>验证中英文会员时间格式与用户指定的中文精确样式，避免把分钟倒计时格式回归为简写。</summary>
    private static void CheckMembershipPresentation()
    {
        var now = new DateTimeOffset(2026, 9, 22, 11, 44, 0, TimeSpan.FromHours(8));
        var expiry = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.FromHours(8));
        var localNow = now.ToLocalTime();
        var localExpiry = expiry.ToLocalTime();
        var remaining = localExpiry - localNow;
        var expectedChinese = $"到期：{localExpiry:yyyy/MM/dd HH:mm:ss}(剩余{(int)remaining.TotalDays}天{remaining.Hours}小时{remaining.Minutes}分)";
        var expectedEnglish = $"Expires: {localExpiry:yyyy/MM/dd HH:mm:ss} ({(int)remaining.TotalDays}d {remaining.Hours}h {remaining.Minutes}m left)";
        Check(QuotaViewModel.FormatMembershipExpiry(expiry, now, AppLanguage.SimplifiedChinese)
            == expectedChinese, "中文会员到期格式精确匹配");
        Check(QuotaViewModel.FormatRemainingCountdown(expiry, now, AppLanguage.SimplifiedChinese)
            == "剩余0天0小时16分", "中文会员正文只显示剩余时间");
        Check(QuotaViewModel.FormatExpiryCountdown(expiry, now, AppLanguage.SimplifiedChinese)
            == expectedChinese, "中文额度条到期格式精确匹配");
        Check(QuotaViewModel.FormatMembershipExpiry(expiry, now, AppLanguage.English)
            == expectedEnglish, "英文会员到期格式正确");
        Check(QuotaViewModel.GetQuotaTone(10) == "Green" && QuotaViewModel.GetQuotaTone(25) == "Purple" && QuotaViewModel.GetQuotaTone(50) == "Yellow" && QuotaViewModel.GetQuotaTone(75) == "Red", "额度颜色按25%区间切换");
        Check(LocalizedTextProvider.Get(AppLanguage.English).SettingsTab == "Settings", "英文设置页文本可用");
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
        var provider = new CodexQuotaProvider(GetTestExecutablePath());
        try
        {
            Environment.SetEnvironmentVariable("QUOTA_CHECK_MODE", "success");
            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);
            Check(snapshot.FiveHour is null && snapshot.SevenDay?.Percent == 45 && snapshot.ResetCredits == 2
                && snapshot.MembershipExpiresAt == DateTimeOffset.FromUnixTimeSeconds(1893456000).ToLocalTime(),
                "严格握手、自动会员到期、仅周额度和重置次数");
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

    /// <summary>返回测试项目的 Windows apphost，避免以 <c>dotnet QuotaChecks.dll</c> 启动时把 dotnet CLI 误作协议服务端。</summary>
    private static string GetTestExecutablePath()
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, "QuotaChecks.exe");
        if (File.Exists(appHost))
        {
            return appHost;
        }

        return Environment.ProcessPath ?? throw new InvalidOperationException("无法定位协议测试子进程。");
    }

    /// <summary>用真实 WPF Dispatcher 验证自动刷新、语言状态通知、错误恢复及关闭后的停止。</summary>
    private static void CheckDispatcher(IQuotaProvider provider, bool live)
    {
        // 验收默认十秒设置在 CheckSettings 中覆盖；离线回归使用短 Tick，真实 app-server 则预留足够时间避免网络读取尚未完成时被下一次 Tick 跳过。
        var refreshIntervalSeconds = live ? 5 : 2;
        var verificationSeconds = live ? 16 : 6;
        var viewModel = new QuotaViewModel(provider, refreshIntervalSeconds: refreshIntervalSeconds);
        var frame = new DispatcherFrame();
        var updates = 0;
        var transientErrorWasShown = false;
        Exception? failure = null;
        var started = DateTimeOffset.Now;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != string.Empty) return;
            transientErrorWasShown |= viewModel.ErrorText.Length > 0 && viewModel.ConnectionText == "连接异常";
            if (viewModel.ErrorText.Length == 0)
            {
                updates++;
                Console.WriteLine($"REFRESH success={updates} elapsed={(DateTimeOffset.Now - started).TotalSeconds:0.0}s");
            }
        };
        var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(verificationSeconds) };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            try
            {
                Check(viewModel.ErrorText.Length == 0, "最后一次刷新无错误");
                Check(viewModel.SevenDayVisibility == Visibility.Visible, "真实绑定显示周额度");
                Check(updates >= 2, "观察到两次自动成功刷新");
                if (!live) Check(((CountingProvider)provider).Calls == 3, "启动和两次自动刷新恰好三次调用");
                if (!live) Check(!transientErrorWasShown, "单次读取失败不显示连接异常");
                if (!live)
                {
                    // 离线提供器同步完成；真实网络读取可能恰好处于下一次刷新，不能假定它空闲。
                    var connectionNotified = false;
                    viewModel.PropertyChanged += (_, change) => connectionNotified |= change.PropertyName == nameof(QuotaViewModel.ConnectionText);
                    viewModel.ApplySettings(new WidgetSettings(refreshIntervalSeconds, AppLanguage.English));
                    Check(connectionNotified && viewModel.ConnectionText == viewModel.Text.LiveStatus, "语言切换立即通知连接状态");
                    Check(viewModel.CanRefresh, "刷新完成后按钮恢复可用");
                }
                viewModel.Dispose();
                Check(!viewModel.CanRefresh, "释放后按钮不再允许刷新");
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
                var account = mode == "signed-out" ? "null" : mode == "api-key" ? """{"type":"apiKey"}""" : """{"type":"chatgpt","planType":"pro","membershipExpiresAt":1893456000}""";
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

    /// <summary>第一次返回有效快照，后续固定失败，用于锁定旧数据显示和三次失败阈值。</summary>
    private sealed class StaleSnapshotProvider : IQuotaProvider
    {
        public int Calls { get; private set; }

        /// <summary>返回一次可显示的周额度，随后模拟网络超时且不修改已有快照。</summary>
        public Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                return Task.FromResult(new QuotaSnapshot("stale", "Pro", null, new QuotaWindow("7d", 55, DateTimeOffset.Now.AddDays(1)), null, 1));
            }

            throw new QuotaReadException("读取超时", "测试用的瞬时网络失败");
        }
    }
}
