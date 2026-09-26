namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 天车通信驱动。
/// 负责写目标位置与动作指令、读天车 X/Y/Z 坐标、到位、报警、伴信号等。
/// 天车三轴运动由天车 PLC 内部完成，上位机只负责下发目标与动作（架构文档 §2.1）。
/// 具体点位定义在天车代码类中。
///
/// 命令方法统一返回 <see cref="CraneCommandResult"/>：**预期内的失败不抛异常**，
/// 由调用方（手动页 / 自动引擎）按 <see cref="CraneCommandResult.IsSuccess"/> 与结果码自行判断。
/// 手动操作页与自动引擎共用同一套方法（同地址、同方法）。
/// </summary>
public interface ICraneDriver : IPlcDriver
{
    /// <summary>天车号，与 CraneConfig.Id 对应。</summary>
    int CraneId { get; }

    /// <summary>
    /// 执行绝对/相对运动：写目标 → 按 Z 方向脉冲触发 → 轮询到位。
    /// 采用 <paramref name="pollEncoder"/> 指定用绝对编码器还是显示值判定到位。
    /// Z 下降时叠加 X2 下压保护（下压到位也视为成功）。
    /// </summary>
    /// <param name="writeX">写入 X 目标的值（调用方按业务换算）</param>
    /// <param name="writeY">写入 Y 目标的值</param>
    /// <param name="writeZ">写入 Z 目标的值</param>
    /// <param name="targetX">X 到位目标（轮询比较用）</param>
    /// <param name="targetY">Y 到位目标</param>
    /// <param name="targetZ">Z 到位目标</param>
    /// <param name="pollEncoder">true=轮询绝对编码器、false=轮询显示值</param>
    /// <param name="zDirection">Z 方向：1=下降 2=上升 0=不动</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>InPosition / PressureReached 为成功；Timeout / PressureOutOfRange 等为失败</returns>
    Task<CraneCommandResult> MoveAsync(int writeX, int writeY, int writeZ,
        int targetX, int targetY, int targetZ,
        bool pollEncoder, int zDirection, CancellationToken cancellationToken = default);

    /// <summary>PLC 原生相对运动（点动）：只下发，不等到位。</summary>
    /// <param name="axis">轴：'X'/'Y'/'Z'</param>
    /// <param name="distance">距离（正数）</param>
    /// <param name="positive">方向：true 正向</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> JogAsync(char axis, int distance, bool positive, CancellationToken cancellationToken = default);

    /// <summary>单轴回原点。</summary>
    /// <param name="axis">轴：'X'/'Y'/'Z'</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> HomeAsync(char axis, CancellationToken cancellationToken = default);

    /// <summary>设置三轴绝对速度/加速度/减速度。</summary>
    /// <param name="speedX">X 速度</param>
    /// <param name="accelX">X 加速度</param>
    /// <param name="decelX">X 减速度</param>
    /// <param name="speedY">Y 速度</param>
    /// <param name="accelY">Y 加速度</param>
    /// <param name="decelY">Y 减速度</param>
    /// <param name="speedZ">Z 速度</param>
    /// <param name="accelZ">Z 加速度</param>
    /// <param name="decelZ">Z 减速度</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> SetAbsoluteSpeedAsync(int speedX, int accelX, int decelX,
        int speedY, int accelY, int decelY, int speedZ, int accelZ, int decelZ,
        CancellationToken cancellationToken = default);

    /// <summary>充磁（轮询 X6 反馈）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> MagnetOnAsync(CancellationToken cancellationToken = default);

    /// <summary>退磁（轮询 X7 反馈）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> MagnetOffAsync(CancellationToken cancellationToken = default);

    /// <summary>打开接液盘。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> DrainOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>关闭接液盘。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> DrainCloseAsync(CancellationToken cancellationToken = default);

    /// <summary>伺服通电。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> ServoOnAsync(CancellationToken cancellationToken = default);

    /// <summary>伺服断电。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> ServoOffAsync(CancellationToken cancellationToken = default);

    /// <summary>减速停止。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> SlowStopAsync(CancellationToken cancellationToken = default);

    /// <summary>急停（不切手动，紧急直写）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> EmergencyStopAsync(CancellationToken cancellationToken = default);

    /// <summary>清除报警。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> ClearAlarmAsync(CancellationToken cancellationToken = default);
}
