using System.Windows.Threading;

namespace AutomatedLine.Ui.Services;

/// <summary>
/// 基于 <see cref="DispatcherTimer"/> 的秒级节拍器。
/// 在 UI 线程触发 Tick，订阅者可直接更新绑定集合，无需自己切换线程。
/// 刷新频率刻意压到 1 秒：现场状态变化对操作员而言不需要毫秒级，降低无谓的界面重绘。
/// </summary>
public sealed class RuntimeTicker : IRuntimeTicker
{
    /// <summary>底层计时器。</summary>
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// 构造函数。在构造时（UI 线程）启动计时器。
    /// </summary>
    public RuntimeTicker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
        _timer.Start();
    }

    /// <inheritdoc />
    public event EventHandler? Tick;
}
