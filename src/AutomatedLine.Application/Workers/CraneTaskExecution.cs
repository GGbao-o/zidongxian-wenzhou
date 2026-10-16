using AutomatedLine.Domain.Tasks;

namespace AutomatedLine.Application.Workers;

/// <summary>一次搬运的取消范围；隔离人工中止与宿主停止，并协调中止和正常完成的竞争。 </summary>
/// 单次天车搬运任务执行上下文。  sealed 代表这个类不允许被继承。
public sealed class CraneTaskExecution : IAsyncDisposable
{
    private readonly object _sync = new();//线程同步锁
    private readonly CancellationTokenSource _cancellation;//本次搬运的取消源
    private Task _cancelCallbacks = System.Threading.Tasks.Task.CompletedTask;//记录异步取消回调的执行任务
    private bool _closed;//本次执行是否已经关闭
    private bool _manualAbortRequested;//是否已接受人工中止

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
/// <summary>  internal 表示默认只能由同一个程序集内的代码调用（不考虑特殊友元程序集配置）。
/// 人工中止任务
/// </summary>
/// <param name="pauseLine"></param>
/// <returns></returns>
    internal bool TryRequestManualAbort(Action pauseLine)    
    {
        lock (_sync)
        {
            if (_closed || _manualAbortRequested) return false;
            // 先暂停派发，再取消本次流程；回调异步执行，避免在 UI 线程内推进故障清理。
            pauseLine();
            //  标记为人工中止
            _manualAbortRequested = true;
            // 发出取消请求，并记录取消回调任务
            _cancelCallbacks = _cancellation.CancelAsync();// 异步触发取消
            return true;//返回 true 表示“这次中止请求被接受了”
        }
    }
/// <summary>
/// 用于正常收尾时防止“人工中止已被接受，却被改回 Completed/Waiting”。
/// </summary>
/// <exception cref="OperationCanceledException"></exception>
    internal void   ThrowIfManualAbortRequested()
    {
        lock (_sync)
        {
            if (_manualAbortRequested) throw new OperationCanceledException("人工中止当前任务", Token);
        }
    }
/// <summary>
/// 人工中止 与 正常完成 只能有一个胜出。
/// 如果已经接受人工中止，正常完成会被拒绝。
/// </summary>
/// <param name="commitCompletion"></param>
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
