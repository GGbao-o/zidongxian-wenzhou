using AutomatedLine.Domain.Tasks;

namespace AutomatedLine.Application.Workers;

/// <summary>一次搬运的取消范围；隔离人工中止与宿主停止，并协调中止和正常完成的竞争。</summary>
public sealed class CraneTaskExecution : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private Task _cancelCallbacks = System.Threading.Tasks.Task.CompletedTask;
    private bool _closed;
    private bool _manualAbortRequested;

    /// <summary>建立本次搬运独享的令牌；宿主停止仍会传入本次流程。</summary>
    /// <param name="task">本次搬运任务</param>
    /// <param name="hostToken">Worker 的宿主取消令牌</param>
    public CraneTaskExecution(TransportTask task, CancellationToken hostToken)
    {
        Task = task;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
    }

    /// <summary>本次搬运任务；用于拒绝界面发出的过期中止请求。</summary>
    public TransportTask Task { get; }

    /// <summary>仅用于本次搬运的取消令牌。</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>是否已接受人工中止；与普通故障、宿主停止分开判断。</summary>
    public bool IsManualAbortRequested { get { lock (_sync) return _manualAbortRequested; } }

    internal bool IsActive { get { lock (_sync) return !_closed; } }

    internal bool TryRequestManualAbort(Action pauseLine)
    {
        lock (_sync)
        {
            if (_closed || _manualAbortRequested) return false;
            // 先暂停派发，再取消本次流程；回调异步执行，避免在 UI 线程内推进故障清理。
            pauseLine();
            _manualAbortRequested = true;
            _cancelCallbacks = _cancellation.CancelAsync();
            return true;
        }
    }

    internal void ThrowIfManualAbortRequested()
    {
        lock (_sync)
        {
            if (_manualAbortRequested) throw new OperationCanceledException("人工中止当前任务", Token);
        }
    }

    internal void Complete(Action commitCompletion)
    {
        lock (_sync)
        {
            // 正常收尾与人工中止只能有一个胜出，防止已接受中止却被改回 Completed/Waiting。
            ThrowIfManualAbortRequested();
            commitCompletion();
            _closed = true;
        }
    }

    internal bool Close()
    {
        lock (_sync)
        {
            _closed = true;
            return _manualAbortRequested;
        }
    }

    /// <summary>退出本次搬运，等待中止回调结束后释放令牌，避免与按钮请求并发释放。</summary>
    /// <returns>异步释放任务</returns>
    public async ValueTask DisposeAsync()
    {
        Task callbacks;
        lock (_sync)
        {
            _closed = true;
            callbacks = _cancelCallbacks;
        }
        try { await callbacks.ConfigureAwait(false); }
        finally { _cancellation.Dispose(); }
    }
}
