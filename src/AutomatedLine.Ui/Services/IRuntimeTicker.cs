namespace AutomatedLine.Ui.Services;

/// <summary>
/// 界面运行态刷新节拍器。
/// 采集是 500ms 一次，但界面没必要同频刷新；统一用一个秒级节拍通知各页面刷新，
/// 各页面订阅同一个事件，既避免多个定时器，也便于控制刷新频率（架构文档性能约定）。
/// </summary>
public interface IRuntimeTicker
{
    /// <summary>每秒触发一次（在 UI 线程）。</summary>
    event EventHandler? Tick;
}
