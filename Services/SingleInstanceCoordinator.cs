using System.Security.Principal;
using System.Windows.Threading;

namespace CodexQuotaWidget.Services;

/// <summary>
/// 通过当前 Windows 用户会话内的命名 Mutex 和命名事件保证小组件只有一个进程；后续启动只会唤醒已有窗口。
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string NamespacePrefix = @"Local\CodexQuotaWidget.";
    private readonly string mutexName;
    private readonly string activationEventName;
    private Mutex? ownershipMutex;
    private EventWaitHandle? activationEvent;
    private RegisteredWaitHandle? activationRegistration;
    private bool isPrimary;
    private bool disposed;

    /// <summary>创建一组可测试的命名内核对象；未指定作用域时按当前用户 SID 隔离不同 Windows 用户。</summary>
    internal SingleInstanceCoordinator(string? scope = null)
    {
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? GetCurrentUserScope() : scope;
        mutexName = NamespacePrefix + normalizedScope + ".Mutex";
        activationEventName = NamespacePrefix + normalizedScope + ".Activate";
    }

    /// <summary>尝试取得主实例所有权；成功后立即创建激活事件，避免后续实例在启动竞态中找不到通知通道。</summary>
    internal bool TryBecomePrimary()
    {
        ThrowIfDisposed();
        if (ownershipMutex is not null)
        {
            return isPrimary;
        }

        var candidate = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            candidate.Dispose();
            return false;
        }

        try
        {
            activationEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, activationEventName);
            ownershipMutex = candidate;
            isPrimary = true;
            return true;
        }
        catch
        {
            candidate.ReleaseMutex();
            candidate.Dispose();
            throw;
        }
    }

    /// <summary>向已存在的主实例发出激活请求；在主实例刚取得 Mutex 的短暂窗口内重试，随后由调用方安静退出。</summary>
    internal bool SignalPrimary(TimeSpan retryTimeout)
    {
        ThrowIfDisposed();
        var deadline = DateTime.UtcNow.Add(retryTimeout);
        do
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(activationEventName);
                return existingEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 主实例可能正处于创建命名事件的极短阶段；重试能避免第二次启动被无声丢弃。
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            Thread.Sleep(50);
        } while (DateTime.UtcNow < deadline);

        return false;
    }

    /// <summary>在主实例中监听来自后续进程的事件，并始终切回 WPF Dispatcher 后再操作窗口。</summary>
    internal void BeginActivationListener(Dispatcher dispatcher, Action activatePrimaryWindow)
    {
        ThrowIfDisposed();
        if (!isPrimary || activationEvent is null)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation requests.");
        }

        if (activationRegistration is not null)
        {
            return;
        }

        activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            (_, timedOut) =>
            {
                if (!timedOut && !dispatcher.HasShutdownStarted)
                {
                    try
                    {
                        _ = dispatcher.BeginInvoke(activatePrimaryWindow);
                    }
                    catch (InvalidOperationException)
                    {
                        // Dispatcher 正在关闭时无需再激活窗口；次实例仍会正常退出。
                    }
                    catch (TaskCanceledException)
                    {
                        // 退出竞态取消了排队回调，不应让线程池回调成为未观察异常。
                    }
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>解除事件监听并释放 Mutex；应用退出时必须在取得 Mutex 的 UI 线程完成，确保下一次启动可立即成为主实例。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        activationRegistration?.Unregister(waitObject: null);
        activationRegistration = null;
        activationEvent?.Dispose();
        activationEvent = null;
        if (isPrimary && ownershipMutex is not null)
        {
            try
            {
                ownershipMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 进程异常终止或错误线程释放时，内核仍会回收 Mutex；不能阻断正常退出。
            }
        }

        ownershipMutex?.Dispose();
        ownershipMutex = null;
        isPrimary = false;
    }

    /// <summary>返回稳定的 SID 作用域，避免不同 Windows 用户共享同一个小组件激活通道。</summary>
    private static string GetCurrentUserScope()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch (PlatformNotSupportedException)
        {
            return Environment.UserName;
        }
    }

    /// <summary>阻止释放后的协调器被重复用于创建或通知实例，避免掩盖生命周期错误。</summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
