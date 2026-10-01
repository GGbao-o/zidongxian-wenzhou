using System.Collections.Concurrent;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;
using FanucFocas;

namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 斜床 FOCAS 通信通道（沈阳斜床，FANUC 自定义协议）。
/// <para>
/// FOCAS 句柄必须由**同一个 OS 线程**创建与使用，跨线程调用会返回 EW_HANDLE(-8)。
/// 因此本类持有一个专用后台线程：所有 Connect/Disconnect/读宏/写宏请求都入队，
/// 由该线程串行执行；调用方（如采集器线程池）只 await 结果。
/// </para>
/// <para>超时单位为**秒**（FOCAS 官方约定）。</para>
/// </summary>
public sealed class FocasLatheChannel : IDisposable
{
    /// <summary>CNC IP（来自数据库）。</summary>
    private readonly string _ip;

    /// <summary>FOCAS 端口（写死 8193）。</summary>
    private readonly ushort _port;

    /// <summary>连接超时（秒）。</summary>
    private readonly int _connectTimeoutSeconds;

    /// <summary>会话操作超时（秒）。</summary>
    private readonly int _operationTimeoutSeconds;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>FOCAS 高层封装（仅允许在专用线程中调用）。</summary>
    private readonly FanucSdk _sdk = new();

    /// <summary>命令队列。</summary>
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());

    /// <summary>Worker 线程。</summary>
    private Thread? _worker;

    /// <summary>Worker 线程 ID（用于断言同线程）。</summary>
    private int _workerThreadId;

    /// <summary>生命周期锁。</summary>
    private readonly object _lifecycleLock = new();

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="ip">CNC IP（来自数据库）</param>
    /// <param name="logger">日志</param>
    /// <param name="port">端口，默认 8193</param>
    /// <param name="connectTimeoutSeconds">连接超时（秒），默认 3</param>
    /// <param name="operationTimeoutSeconds">会话操作超时（秒），默认 3</param>
    public FocasLatheChannel(string ip, IAppLogger logger, int port = 8193,
        int connectTimeoutSeconds = 3, int operationTimeoutSeconds = 3)
    {
        _ip = ip;
        _logger = logger;
        _port = (ushort)port;
        _connectTimeoutSeconds = Math.Max(1, connectTimeoutSeconds);
        _operationTimeoutSeconds = Math.Max(1, operationTimeoutSeconds);
    }

    /// <summary>当前是否已连接（只读托管状态，不调用原生函数）。</summary>
    public bool IsConnected
    {
        get
        {
            try
            {
                return _sdk.IsConnected;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>主动建立连接。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            EnsureConnectedOnWorker();
            return true;
        }, cancellationToken);

    /// <summary>主动断开连接。</summary>
    public Task DisconnectAsync()
        => InvokeAsync(() => { _sdk.Disconnect(); return true; });

    /// <summary>读单个宏变量。</summary>
    /// <param name="macro">宏变量编号（#编号）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>宏变量浮点值</returns>
    public Task<double> ReadMacroAsync(int macro, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            EnsureConnectedOnWorker();
            return _sdk.GetMacro(macro);
        }, cancellationToken);

    /// <summary>批量读宏变量。</summary>
    /// <param name="macros">宏变量编号数组</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>与输入顺序一致的值数组</returns>
    public Task<double[]> ReadMacrosAsync(IReadOnlyList<int> macros, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            EnsureConnectedOnWorker();
            var result = new double[macros.Count];
            for (var i = 0; i < macros.Count; i++)
            {
                result[i] = _sdk.GetMacro(macros[i]);
            }

            return result;
        }, cancellationToken);

    /// <summary>写单个宏变量。</summary>
    /// <param name="macro">宏变量编号</param>
    /// <param name="value">值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task WriteMacroAsync(int macro, double value, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            EnsureConnectedOnWorker();
            _sdk.SetMacro(macro, value);
            return true;
        }, cancellationToken);

    /// <summary>
    /// 把操作入队到专用线程执行，并等待结果。
    /// </summary>
    /// <typeparam name="T">返回类型</typeparam>
    /// <param name="operation">要在专用线程执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    private Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureWorkerStarted();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                if (completion.Task.IsCompleted)
                {
                    return;
                }

                try
                {
                    completion.TrySetResult(operation());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetException(new ObjectDisposedException(nameof(FocasLatheChannel)));
        }

        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    /// <summary>启动专用线程（幂等）。</summary>
    private void EnsureWorkerStarted()
    {
        lock (_lifecycleLock)
        {
            if (_worker is { IsAlive: true })
            {
                return;
            }

            _worker = new Thread(WorkerLoop)
            {
                Name = $"FOCAS-{_ip}",
                IsBackground = true
            };
            _worker.Start();
        }
    }

    /// <summary>专用线程主循环：串行执行队列中的操作。</summary>
    private void WorkerLoop()
    {
        _workerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }
        catch (ObjectDisposedException)
        {
            // 队列释放时退出
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.Communication, $"斜床[{_ip}] FOCAS 通信线程异常退出", ex);
        }
        finally
        {
            try
            {
                _sdk.Disconnect();
            }
            catch
            {
                // 忽略关闭异常
            }
        }
    }

    /// <summary>仅可在专用线程调用；确保已连接并设置会话超时。</summary>
    private void EnsureConnectedOnWorker()
    {
        if (_sdk.IsConnected)
        {
            return;
        }

        if (!_sdk.Connect(_ip, _port, _connectTimeoutSeconds))
        {
            throw new InvalidOperationException($"FANUC 连接失败：{_ip}:{_port}（超时 {_connectTimeoutSeconds}s）");
        }

        // 会话操作超时（秒）
        Focas1.cnc_settimeout(_sdk.Handle, _operationTimeoutSeconds);
        _logger.Info(LogCategory.Communication, $"斜床[{_ip}] FOCAS 已连接（超时 {_operationTimeoutSeconds}s，线程 {_workerThreadId}）");
    }

    /// <summary>已释放则抛异常。</summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FocasLatheChannel));
        }
    }

    /// <summary>释放：停止线程并释放句柄。</summary>
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            // 让线程退出：先断开（入队会失败，因为已 disposed？——改用直接入队忽略）
            _queue.CompleteAdding();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _worker?.Join(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 忽略
        }

        _queue.Dispose();
    }
}
