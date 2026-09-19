namespace AutomatedLine.Domain.Signals;

/// <summary>
/// 快照数据质量。用于界面对"数据是否可信"的展示与后续保护逻辑。
/// </summary>
public enum SignalQuality
{
    /// <summary>良好：本次采集完整成功。</summary>
    Good = 0,

    /// <summary>过期：数据来自较早的快照，可能已不反映现场。</summary>
    Stale = 1,

    /// <summary>差：采集失败或数据不可信。</summary>
    Bad = 2
}
