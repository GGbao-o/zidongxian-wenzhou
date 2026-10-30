using System.Runtime.Versioning;

namespace AutomatedLine.Ui.Services;

/// <summary>
/// 在初始化前限制同一产线应用只保留一个实例，并将重复打开请求通知给现有窗口。
/// 命名Mutex只利用内核对象的存活期判重，不取得线程所有权，避免异步退出时跨线程释放。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstanceService : IDisposable
{
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _guard;
    private EventWaitHandle? _wake;
    private Task? _listener;
    private int _disposed;

    /// <summary>创建实例保护器；生产名称固定且不依赖安装目录，覆盖本机不同登录会话。</summary>
    /// <param name="name">内核对象前缀；测试必须使用独立名称，不能占用生产保护。</param>
    public SingleInstanceService(string name = @"Global\AutomatedLine.LineController.v1") => _name = name;

    /// <summary>原子创建保护对象；已有对象时立即释放本次句柄，不进入任何业务初始化。</summary>
    /// <returns>本进程是唯一实例返回true；已有实例返回false。</returns>
    public bool TryAcquire()
    {
        if (_guard is not null) throw new InvalidOperationException("实例保护不能重复申请");
        var guard = new Mutex(false, _name + ".Instance", out var created);
        if (!created)
        {
            guard.Dispose();
            return false;
        }

        _guard = guard;
        try
        {
            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, _name + ".Activate");
            return true;
        }
        catch
        {
            _guard.Dispose();
            _guard = null;
            throw;
        }
    }

    /// <summary>启动窗口唤醒监听；回调必须快速投递到UI，不能同步等待UI线程。</summary>
    /// <param name="activate">收到重复打开请求时执行的回调。</param>
    public void StartListening(Action activate)
    {
        if (_wake is null || _listener is not null) throw new InvalidOperationException("实例监听状态不正确");
        _listener = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _stop.Token.WaitHandle, _wake };
            while (WaitHandle.WaitAny(handles) == 1) activate();
        });
    }

    /// <summary>有界重试通知已有进程；覆盖Mutex已创建但唤醒事件尚未就绪的短暂窗口。</summary>
    /// <param name="cancellationToken">应用取消令牌。</param>
    /// <returns>通知已发送返回true；原实例退出或事件仍不可用返回false。</returns>
    public async Task<bool> NotifyExistingAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var wake = EventWaitHandle.OpenExisting(_name + ".Activate");
                return wake.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        return false;
    }

    /// <summary>先结束监听再释放内核句柄；必须在业务服务停止后调用。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _listener?.GetAwaiter().GetResult(); }
        finally
        {
            // 即使回调失败，也不能让异常跳过句柄释放而留下无法重新启动的实例保护。
            _wake?.Dispose();
            _guard?.Dispose();
            _stop.Dispose();
        }
    }
}
