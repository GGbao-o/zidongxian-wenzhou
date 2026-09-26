namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 天车命令结果码。
/// 统一用"正常返回 + 结果码"表达命令结局，取代"成功用 bool、失败抛异常"的混用方式：
/// 预期内的失败（超时、下压超差、通信失败）是结果，不是异常；只有 bug 类异常才冒泡。
/// </summary>
public enum CraneCommandCode
{
    // ── 成功 ──

    /// <summary>XYZ 均在到位容差内（普通到位）。</summary>
    InPosition,

    /// <summary>下压限位 X2 触发，且 Z 在下压容差内（正常下压到位）。</summary>
    PressureReached,

    /// <summary>通用成功（回原点/点动下发/充放磁/接液盘/伺服/急停/清报警/设速度）。</summary>
    Completed,

    // ── 失败 ──

    /// <summary>运动超时（未到位）。</summary>
    Timeout,

    /// <summary>下压限位触发，但 Z 与目标差超出下压容差（压到了却没到目标）。</summary>
    PressureOutOfRange,

    /// <summary>充放磁反馈超时（多为重试次数用尽）。</summary>
    FeedbackTimeout,

    /// <summary>未连接。</summary>
    NotConnected,

    /// <summary>PLC / 伺服报警。</summary>
    Alarm,

    /// <summary>通信失败（底层协议异常在此层转码）。</summary>
    Communication,

    /// <summary>前置条件不满足（如相对值全为 0、轴名非法）。</summary>
    Rejected
}

/// <summary>
/// 天车命令统一返回：是否成功 + 结果码 + 说明。
/// <see cref="IsSuccess"/> 为 true 时 <see cref="Code"/> 只可能是
/// <see cref="CraneCommandCode.InPosition"/> / <see cref="CraneCommandCode.PressureReached"/> /
/// <see cref="CraneCommandCode.Completed"/>。
/// </summary>
/// <param name="IsSuccess">是否成功</param>
/// <param name="Code">结果码</param>
/// <param name="Message">说明（含关键坐标/差值，供日志与界面显示）</param>
public readonly record struct CraneCommandResult(bool IsSuccess, CraneCommandCode Code, string Message)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="code">成功码</param>
    /// <param name="message">说明</param>
    /// <returns>成功结果</returns>
    public static CraneCommandResult Ok(CraneCommandCode code, string message) => new(true, code, message);

    /// <summary>构造失败结果。</summary>
    /// <param name="code">失败码</param>
    /// <param name="message">说明</param>
    /// <returns>失败结果</returns>
    public static CraneCommandResult Fail(CraneCommandCode code, string message) => new(false, code, message);
}
