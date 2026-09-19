namespace AutomatedLine.Domain.Signals;

/// <summary>
/// 一次采集得到的信号快照。
/// 采用"整体替换"而非逐字段更新，保证同一份快照来自同一采集时刻（架构文档 §8.2）。
/// </summary>
/// <param name="CapturedAt">采集时间</param>
/// <param name="Sequence">递增版本号，用于判断快照新旧</param>
/// <param name="ConnectionState">连接状态</param>
/// <param name="Quality">数据质量</param>
/// <param name="Values">点名 → 值的只读字典</param>
public sealed record SignalSnapshot(
    DateTime CapturedAt,
    long Sequence,
    ConnectionState ConnectionState,
    SignalQuality Quality,
    IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>是否已连接。</summary>
    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    /// <summary>
    /// 读取布尔信号；不存在或类型不符时返回 false（保守取"无"）。
    /// </summary>
    /// <param name="name">逻辑点名</param>
    /// <returns>布尔值</returns>
    public bool GetBool(string name)
        => Values.TryGetValue(name, out var value) && value is bool b && b;

    /// <summary>
    /// 读取整型信号；不存在时返回 0。
    /// </summary>
    /// <param name="name">逻辑点名</param>
    /// <returns>整数值</returns>
    public int GetInt(string name)
        => Values.TryGetValue(name, out var value) && value is not null ? Convert.ToInt32(value) : 0;

    /// <summary>
    /// 读取浮点信号；不存在时返回 0。
    /// </summary>
    /// <param name="name">逻辑点名</param>
    /// <returns>浮点值</returns>
    public double GetDouble(string name)
        => Values.TryGetValue(name, out var value) && value is not null ? Convert.ToDouble(value) : 0d;

    /// <summary>
    /// 读取字符串信号；不存在时返回空串。
    /// </summary>
    /// <param name="name">逻辑点名</param>
    /// <returns>字符串值</returns>
    public string GetString(string name)
        => Values.TryGetValue(name, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}
