namespace AutomatedLine.Domain.Signals;

/// <summary>
/// 信号数据类型。点位定义（含地址）写在设备/天车代码类中，不进数据库。
/// </summary>
public enum SignalDataType
{
    /// <summary>布尔量（位）。</summary>
    Bool = 0,

    /// <summary>16 位整型。</summary>
    Int = 1,

    /// <summary>32 位整型。</summary>
    Int32 = 2,

    /// <summary>浮点数。</summary>
    Double = 3,

    /// <summary>字符串。</summary>
    String = 4
}
