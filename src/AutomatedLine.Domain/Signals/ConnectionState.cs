namespace AutomatedLine.Domain.Signals;

/// <summary>
/// 通信连接状态。
/// 断开属于**瞬时故障**：只标记断开并触发重连重试，不丢弃任务、不暂停整线（架构文档 §10.4）。
/// </summary>
public enum ConnectionState
{
    /// <summary>已连接。</summary>
    Connected = 0,

    /// <summary>已断开/不可达。</summary>
    Disconnected = 1
}
