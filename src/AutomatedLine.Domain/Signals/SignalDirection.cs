namespace AutomatedLine.Domain.Signals;

/// <summary>
/// 信号读写方向。用于说明某个点位是采集、下发还是双向。
/// </summary>
public enum SignalDirection
{
    /// <summary>只读（采集）。</summary>
    Read = 0,

    /// <summary>只写（下发）。</summary>
    Write = 1,

    /// <summary>读/写。</summary>
    ReadWrite = 2
}
