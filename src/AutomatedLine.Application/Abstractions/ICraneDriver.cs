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
    /// <summary>手动按钮专用：每次依次写D4001=2、D4500=2，不使用或修改自动动作的手动前置缓存。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>两次写入应答均成功才返回成功；不代表PLC模式已读回确认。</returns>
    Task<CraneCommandResult> SetManualModeAsync(CancellationToken cancellationToken = default);

    /// <summary>天车号，与 CraneConfig.Id 对应。</summary>
    int CraneId { get; }

    /// <summary>直接读取一次 X11 有板线圈；读取失败抛异常，不轮询、不读取其他状态。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>X11=1 返回 true，X11=0 返回 false</returns>
    Task<bool> ReadHasPlateAsync(CancellationToken cancellationToken = default);

    
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

    /// <summary>充磁：写2保持200ms后写0，按配置等待（默认1000ms），再单读X6反馈并返回结果。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> MagnetOnAsync(CancellationToken cancellationToken = default);

    /// <summary>退磁：写2保持200ms后写0，按配置等待（默认1000ms），再单读X7反馈并返回结果。</summary>
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

    /// <summary>
    /// 读取天车状态快照（含显示值/绝对编码器、回原点、报警、三轴速度、X 区信号）。
    /// 读取失败返回 null，不抛异常；供手动页与自动引擎共用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>状态快照；读取失败返回 null</returns>
    Task<CraneStatusDto?> ReadStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 绝对移动：输入用户绝对坐标（以绝对编码器为基准）。
    /// 内部读编码器/显示值换算写入值 D3102~D3107，轮询绝对编码器到位。
    /// </summary>
    /// <param name="x">用户输入 X 绝对值</param>
    /// <param name="y">用户输入 Y 绝对值</param>
    /// <param name="z">用户输入 Z 绝对值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> MoveAbsoluteAsync(int x, int y, int z, CancellationToken cancellationToken = default);

    /// <summary>
    /// 相对移动：以显示值为基准，目标 = 显示值 + 相对值（为 0 的轴不动），轮询显示值到位。
    /// </summary>
    /// <param name="dx">X 相对值（0=不动）</param>
    /// <param name="dy">Y 相对值（0=不动）</param>
    /// <param name="dz">Z 相对值（0=不动）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<CraneCommandResult> MoveRelativeAsync(int dx, int dy, int dz, CancellationToken cancellationToken = default);
}
