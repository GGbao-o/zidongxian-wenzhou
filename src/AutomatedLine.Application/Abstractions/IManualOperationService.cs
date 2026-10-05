namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 天车状态快照（手动页显示用）。
/// 显示值与绝对编码器值分别对应不同基准：相对运动用显示值、绝对运动用编码器值。
/// </summary>
public sealed class CraneStatusDto
{
    /// <summary>X 显示值坐标。</summary>
    public int XDisplay { get; init; }

    /// <summary>Y 显示值坐标。</summary>
    public int YDisplay { get; init; }

    /// <summary>Z 显示值坐标。</summary>
    public int ZDisplay { get; init; }

    /// <summary>X 绝对编码器坐标。</summary>
    public int XEncoder { get; init; }

    /// <summary>Y 绝对编码器坐标。</summary>
    public int YEncoder { get; init; }

    /// <summary>Z 绝对编码器坐标。</summary>
    public int ZEncoder { get; init; }

    /// <summary>X 轴已回原点。</summary>
    public bool XHomeCompleted { get; init; }

    /// <summary>Y 轴已回原点。</summary>
    public bool YHomeCompleted { get; init; }

    /// <summary>Z 轴已回原点。</summary>
    public bool ZHomeCompleted { get; init; }

    /// <summary>伺服报警。</summary>
    public int ServoAlarm { get; init; }

    /// <summary>PLC 伺服指令报警。</summary>
    public int PlcServoAlarm { get; init; }

    /// <summary>PLC 报警。</summary>
    public int PlcAlarm { get; init; }

    /// <summary>X 绝对速度。</summary>
    public int XSpeed { get; init; }

    /// <summary>X 绝对加速度。</summary>
    public int XAccel { get; init; }

    /// <summary>X 绝对减速度。</summary>
    public int XDecel { get; init; }

    /// <summary>Y 绝对速度。</summary>
    public int YSpeed { get; init; }

    /// <summary>Y 绝对加速度。</summary>
    public int YAccel { get; init; }

    /// <summary>Y 绝对减速度。</summary>
    public int YDecel { get; init; }

    /// <summary>Z 绝对速度。</summary>
    public int ZSpeed { get; init; }

    /// <summary>Z 绝对加速度。</summary>
    public int ZAccel { get; init; }

    /// <summary>Z 绝对减速度。</summary>
    public int ZDecel { get; init; }

    // 以下 X 区线圈点位均为 bool?：true/false 为实际读到的电平，**null 表示该点读取失败（未知）**。
    // 之所以用可空类型，是因为"读不到"不能等同于"0/无板"——按 0 处理会把通信故障误判成现场状态。

    /// <summary>有版（X11 线圈 63497）。现场确认 D5029 不是有板信号。</summary>
    public bool? HasPlate { get; init; }

    /// <summary>充磁反馈（X6）。</summary>
    public bool? MagnetizeFeedback { get; init; }

    /// <summary>退磁反馈（X7）。</summary>
    public bool? DemagnetizeFeedback { get; init; }

    /// <summary>磁铁下压限位（X2）。</summary>
    public bool? PressureLimit { get; init; }

    /// <summary>接液盘门开到位（X0）。</summary>
    public bool? DrainOpen { get; init; }

    /// <summary>接液盘门关到位（X1）。</summary>
    public bool? DrainClose { get; init; }

    /// <summary>X 轴物理原点输入（X3）。</summary>
    public bool? PhysicalHomeX { get; init; }

    /// <summary>Y 轴物理原点输入（X4）。</summary>
    public bool? PhysicalHomeY { get; init; }

    /// <summary>Z 轴物理原点输入（X5）。</summary>
    public bool? PhysicalHomeZ { get; init; }

    /// <summary>是否任一报警非 0。</summary>
    public bool HasAlarm => ServoAlarm != 0 || PlcServoAlarm != 0 || PlcAlarm != 0;
}

/// <summary>
/// 手动操作结果：成功与否 + 提示信息（供页面显示）。
/// </summary>
/// <param name="Success">是否成功</param>
/// <param name="Message">提示信息</param>
public sealed record ManualOperationResult(bool Success, string Message)
{
    /// <summary>成功结果。</summary>
    /// <param name="message">提示</param>
    /// <returns>结果</returns>
    public static ManualOperationResult Ok(string message) => new(true, message);

    /// <summary>失败结果。</summary>
    /// <param name="message">提示</param>
    /// <returns>结果</returns>
    public static ManualOperationResult Fail(string message) => new(false, message);
}

/// <summary>
/// 三轴绝对速度/加速度/减速度（手动页“设置速度”用）。
/// </summary>
public sealed record CraneSpeedDto(
    int SpeedX, int AccelX, int DecelX,
    int SpeedY, int AccelY, int DecelY,
    int SpeedZ, int AccelZ, int DecelZ);

/// <summary>
/// 手动操作服务：选择天车后读取状态与下发手动命令。
/// 真实实现基于汇川 Modbus TCP；IP 来自数据库，端口/协议写死。
/// </summary>
public interface IManualOperationService
{
    /// <summary>
    /// 读取天车状态快照（内部会自动连接；失败返回 null）。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>状态；无法读取返回 null</returns>
    Task<CraneStatusDto?> ReadStatusAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>设置三轴绝对速度（D2501~D2503 / D2510~D2512 / D2519~D2521）。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="speed">三轴参数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> SetSpeedAsync(int craneId, CraneSpeedDto speed, CancellationToken cancellationToken = default);

    /// <summary>
    /// 绝对移动：输入的是用户绝对目标值（以绝对编码器为基准）。
    /// 写入 D3102~D3107 = 用户绝对值 − 当前编码器值 + 当前显示值；轮询绝对编码器到位。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="x">用户输入 X 绝对值</param>
    /// <param name="y">用户输入 Y 绝对值</param>
    /// <param name="z">用户输入 Z 绝对值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> MoveAbsoluteAsync(int craneId, int x, int y, int z, CancellationToken cancellationToken = default);

    /// <summary>
    /// 相对移动：以显示值为基准，目标 = 显示值 + 相对值（为 0 的轴不参与）；轮询显示值到位。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="dx">X 相对值（0=不动）</param>
    /// <param name="dy">Y 相对值（0=不动）</param>
    /// <param name="dz">Z 相对值（0=不动）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> MoveRelativeAsync(int craneId, int dx, int dy, int dz, CancellationToken cancellationToken = default);

    /// <summary>清除报警。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> ClearAlarmAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>伺服通电。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> ServoOnAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>伺服断电。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> ServoOffAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>减速停止。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> SlowStopAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>急停。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> EmergencyStopAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>单轴回原点。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="axis">轴：'X'/'Y'/'Z'</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> HomeAsync(int craneId, char axis, CancellationToken cancellationToken = default);

    /// <summary>充磁。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> MagnetOnAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>放磁（退磁）。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> MagnetOffAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>打开接液盘。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> DrainOpenAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>关闭接液盘。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    Task<ManualOperationResult> DrainCloseAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 尽力复位指定设备"上位机写入的握手位"（写 0）。
    /// 只有双头镗 / 斜床 / 研磨机需要（其余类型返回失败提示）。
    /// <para>
    /// 用途：异常中止后人工补清——这些位残留 1 会让该设备**下一次上料收不到上升沿**。
    /// 自动线在"取料后中转到目标"时也会自动复位同一批点（共用 <c>ITransferHandler.ClearHandshakeAsync</c>）。
    /// </para>
    /// </summary>
    /// <param name="deviceId">设备号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果；成功时 Message 含复位点数</returns>
    Task<ManualOperationResult> ClearHandshakeAsync(int deviceId, CancellationToken cancellationToken = default);
}
