using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Line;

namespace AutomatedLine.Application.Line;

/// <summary>
/// 线路控制服务：维护整线状态（未启动/运行/暂停/故障暂停）。
/// 调度器与天车执行器都以 <see cref="IsRunning"/> 作为"是否推进"的总开关（架构文档 §8.1）。
/// 线路默认 Stopped，必须人工点击"启动线路"才运行。
/// </summary>
public sealed class LineControlService
{
    /// <summary>并发保护锁。</summary>
    private readonly object _sync = new();

    /// <summary>供生成、调度和各车线程读取的线路状态；volatile保证读写可见性，不代表已进入的流程自动取消。</summary>
    private volatile LineState _state = LineState.Stopped;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="logger">日志</param>
    public LineControlService(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>线路状态变化时触发，供界面刷新。</summary>
    public event EventHandler? StateChanged;

    /// <summary>当前线路状态。</summary>
    public LineState State => _state;

    /// <summary>是否处于自动运行（调度与执行器据此推进）。</summary>
    public bool IsRunning => State == LineState.Running;

    /// <summary>启动线路，进入自动运行。</summary>
    public void Start()
    {
        SetState(LineState.Running);
        _logger.Info(LogCategory.System, "线路已启动，进入自动运行");
    }

    /// <summary>人为暂停。</summary>
    public void Pause()
    {
        SetState(LineState.Paused);
        _logger.Info(LogCategory.System, "线路已暂停");
    }

    /// <summary>
    /// 进入故障暂停（异常处理触发）。
    /// </summary>
    public void EnterFaultPause()
    {
        SetState(LineState.PausedByFault);
        _logger.Warn(LogCategory.System, "线路进入故障暂停，等待人工处理后重新启动");
    }

    /// <summary>设置状态并通知订阅者。</summary>
    /// <param name="state">新状态</param>
    private void SetState(LineState state)
    {
        lock (_sync)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
