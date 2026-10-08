using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 天车状态快照（一次批量读 D5000~D5029 的解析结果，仅含实际使用字段）。
/// </summary>
public sealed class CraneStatus
{
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

    /// <summary>X 显示值坐标。</summary>
    public int XPosition { get; init; }

    /// <summary>Y 显示值坐标。</summary>
    public int YPosition { get; init; }

    /// <summary>Z 显示值坐标。</summary>
    public int ZPosition { get; init; }

    /// <summary>X 绝对编码器坐标。</summary>
    public int XEncoder { get; init; }

    /// <summary>Y 绝对编码器坐标。</summary>
    public int YEncoder { get; init; }

    /// <summary>Z 绝对编码器坐标。</summary>
    public int ZEncoder { get; init; }

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
}

/// <summary>
/// 天车服务（汇川 PLC / Modbus TCP）：封装状态读取与手动操作。
/// 每台天车一个实例；IP 来自数据库，端口/协议/站号在代码中固定。
/// 实现 <see cref="ICraneDriver"/> 以便采集器读取快照（<see cref="ReadAllSignalsAsync"/>）。
/// </summary>
public sealed class CraneService : ICraneDriver
{
    /// <summary>PLC 端口（写死）。</summary>
    private const int PlcPort = 502;

    /// <summary>Modbus 站号（写死）。</summary>
    private const byte UnitId = 1;

    /// <summary>运动超时兜底值（毫秒）：配置非法时使用 60s。</summary>
    private const int FallbackMoveTimeoutMs = 60_000;

    /// <summary>到位容差兜底值（mm）。</summary>
    private const int FallbackPositionToleranceMm = 5;

    /// <summary>下压容差兜底值（mm）。</summary>
    private const int FallbackPressureToleranceMm = 15;

    /// <summary>底层 Modbus 客户端。</summary>
    private readonly ModbusTcpClient _client;

    /// <summary>天车名称（日志用）。</summary>
    private readonly string _name;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>是否已确认处于手动模式（缓存，避免重复写 D4001/D4500）。</summary>
    private bool _manualModeSet;

    /// <summary>运动到位超时（毫秒，来自 appsettings 的 App:Crane）。</summary>
    private readonly int _moveTimeoutMs;

    /// <summary>正常 XYZ 到位容差（mm，来自 appsettings）。</summary>
    private readonly int _positionTolerance;

    /// <summary>下压容差（mm）：仅当 X2 下压触发时用于判断 Z 是否到位。</summary>
    private readonly int _pressureTolerance;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="name">天车名称（日志用）</param>
    /// <param name="ip">PLC IP（来自数据库）</param>
    /// <param name="logger">日志</param>
    /// <param name="configService">程序配置：运动超时与到位/下压容差（来自 appsettings 的 App:Crane）</param>
    public CraneService(int craneId, string name, string ip, IAppLogger logger, IConfigService configService)
    {
        CraneId = craneId;
        _name = name;
        _logger = logger;

        var crane = configService.GetAppOptions().Crane;
        _moveTimeoutMs = crane.MoveTimeoutMs > 0 ? crane.MoveTimeoutMs : FallbackMoveTimeoutMs;
        _positionTolerance = crane.PositionToleranceMm > 0 ? crane.PositionToleranceMm : FallbackPositionToleranceMm;
        _pressureTolerance = crane.PressureToleranceMm > 0 ? crane.PressureToleranceMm : FallbackPressureToleranceMm;

        _client = new ModbusTcpClient(ip, PlcPort, UnitId);
        _logger.Info(LogCategory.System,
            $"天车[{name}] 创建服务，IP={ip}:{PlcPort}（端口/协议固定），运动超时={_moveTimeoutMs}ms，到位容差={_positionTolerance}mm，下压容差={_pressureTolerance}mm");
    }

    /// <inheritdoc />
    public int CraneId { get; }

    /// <inheritdoc />
    public bool IsConnected => _client.IsConnected;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _manualModeSet = false;
        _logger.Info(LogCategory.Communication, $"天车[{_name}] 已连接");
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _client.DisconnectAsync().ConfigureAwait(false);
        _manualModeSet = false;
    }

    /// <summary>读取原始状态快照（D5000~D5029 + 速度区）。内部使用，失败抛异常。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>状态快照</returns>
    private async Task<CraneStatus> ReadRawStatusAsync(CancellationToken cancellationToken = default)
    {
        var r = await _client.ReadRegistersAsync(CraneAddress.StatusStart, CraneAddress.StatusCount, cancellationToken).ConfigureAwait(false);
        // 速度区 D2501~D2521 共 21 个：X=+0~2, Y=+9~11, Z=+18~20
        var spd = await _client.ReadRegistersAsync(CraneAddress.XAbsSpeed, 21, cancellationToken).ConfigureAwait(false);

        return new CraneStatus
        {
            XHomeCompleted = r[6] != 0,
            YHomeCompleted = r[7] != 0,
            ZHomeCompleted = r[8] != 0,
            ServoAlarm = r[11],
            PlcServoAlarm = r[12],
            PlcAlarm = r[13],
            XEncoder = Combine(r[14], r[15]),
            XPosition = Combine(r[18], r[19]),
            YEncoder = r[20],
            YPosition = r[22],
            ZEncoder = r[23],
            ZPosition = r[25],
            XSpeed = spd[0],
            XAccel = spd[1],
            XDecel = spd[2],
            YSpeed = spd[9],
            YAccel = spd[10],
            YDecel = spd[11],
            ZSpeed = spd[18],
            ZAccel = spd[19],
            ZDecel = spd[20]
        };
    }

    /// <summary>读单个 X 线圈。</summary>
    /// <param name="coilAddress">线圈地址（如 X6=63494）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>ON 返回 true</returns>
    public Task<bool> ReadCoilAsync(int coilAddress, CancellationToken cancellationToken = default)
        => _client.ReadCoilAsync(coilAddress, cancellationToken);

    /// <inheritdoc />
    public async Task<CraneStatusDto?> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        // 第一段：批量读状态与速度区。拿不到就没有坐标可显示，返回 null（不抛）。
        CraneStatus s;
        try
        {
            s = await ReadRawStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Communication, $"天车[{_name}] 读取状态失败（D5000~D5029 + 速度区）：{ex.Message}");
            return null;
        }

        // 第二段：X 区线圈逐个读。单点失败只把该点标记为 null（未知），不拖垮整块状态。
        return new CraneStatusDto
        {
            XDisplay = s.XPosition,
            YDisplay = s.YPosition,
            ZDisplay = s.ZPosition,
            XEncoder = s.XEncoder,
            YEncoder = s.YEncoder,
            ZEncoder = s.ZEncoder,
            XHomeCompleted = s.XHomeCompleted,
            YHomeCompleted = s.YHomeCompleted,
            ZHomeCompleted = s.ZHomeCompleted,
            ServoAlarm = s.ServoAlarm,
            PlcServoAlarm = s.PlcServoAlarm,
            PlcAlarm = s.PlcAlarm,
            XSpeed = s.XSpeed,
            XAccel = s.XAccel,
            XDecel = s.XDecel,
            YSpeed = s.YSpeed,
            YAccel = s.YAccel,
            YDecel = s.YDecel,
            ZSpeed = s.ZSpeed,
            ZAccel = s.ZAccel,
            ZDecel = s.ZDecel,
            HasPlate = await ReadCoilOrNullAsync(CraneAddress.X11_HasPlate, "X11 检测有版", cancellationToken).ConfigureAwait(false),
            MagnetizeFeedback = await ReadCoilOrNullAsync(CraneAddress.X6_MagnetizeOk, "X6 充磁反馈", cancellationToken).ConfigureAwait(false),
            DemagnetizeFeedback = await ReadCoilOrNullAsync(CraneAddress.X7_DemagnetizeOk, "X7 退磁反馈", cancellationToken).ConfigureAwait(false),
            PressureLimit = await ReadCoilOrNullAsync(CraneAddress.X2_MagnetDownLimit, "X2 磁铁下压限位", cancellationToken).ConfigureAwait(false),
            DrainOpen = await ReadCoilOrNullAsync(CraneAddress.X0_DrainOpen, "X0 接液盘门开到位", cancellationToken).ConfigureAwait(false),
            DrainClose = await ReadCoilOrNullAsync(CraneAddress.X1_DrainClose, "X1 接液盘门关到位", cancellationToken).ConfigureAwait(false),
            PhysicalHomeX = await ReadCoilOrNullAsync(CraneAddress.X3_HomeX, "X3 X 轴物理原点", cancellationToken).ConfigureAwait(false),
            PhysicalHomeY = await ReadCoilOrNullAsync(CraneAddress.X4_HomeY, "X4 Y 轴物理原点", cancellationToken).ConfigureAwait(false),
            PhysicalHomeZ = await ReadCoilOrNullAsync(CraneAddress.X5_HomeZ, "X5 Z 轴物理原点", cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// 读单个 X 线圈，失败返回 null（未知）并记一条含点名的日志，**绝不按 0 处理**——
    /// 否则通信故障会被误读成"无板 / 未下压"。
    /// </summary>
    /// <param name="address">线圈地址</param>
    /// <param name="name">点位名（日志用）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>true/false 为实际电平；null 表示读取失败</returns>
    private async Task<bool?> ReadCoilOrNullAsync(int address, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.ReadCoilAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Communication, $"天车[{_name}] 读取 {name}（线圈 {address}）失败：{ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />绝对移动   xyz  输入的目标绝对值编码器的值
    public async Task<CraneCommandResult> MoveAbsoluteAsync(int x, int y, int z, CancellationToken cancellationToken = default)
    {
        try
        {
            //读天车寄存器地址 原始状态
            var s = await ReadRawStatusAsync(cancellationToken).ConfigureAwait(false);

            // 写入 D3102~D3107 = 用户绝对值 − 当前编码器值 + 当前显示值；到位轮询绝对编码器
            var writeX = x - (s.XEncoder - s.XPosition);
            var writeY = y - (s.YEncoder - s.YPosition);
            var writeZ = z - (s.ZEncoder - s.ZPosition);

            var dz = z - s.ZEncoder;
            //判断z轴方向
            var zDirection = dz > 0 ? 1 : dz < 0 ? 2 : 0;

            return await MoveAsync(writeX, writeY, writeZ, x, y, z, pollEncoder: true, zDirection, cancellationToken).ConfigureAwait(false);
        }
        //OperationCanceledException：表示"调用方主动取消了"，这不是一个失败结果，而是一个控制信号。它必须向上传播，让调用方（或 Task 调度层）知道这个任务是"被取消"而不是"执行失败"。
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FailCommunication(ex, "绝对移动");
        }
    }

    /// <inheritdoc />相对移动
    public async Task<CraneCommandResult> MoveRelativeAsync(int dx, int dy, int dz, CancellationToken cancellationToken = default)
    {
        if (dx == 0 && dy == 0 && dz == 0)
        {
            return CraneCommandResult.Fail(CraneCommandCode.Rejected, "相对值不能全为 0");
        }

        try
        {
            var s = await ReadRawStatusAsync(cancellationToken).ConfigureAwait(false);

            // 以显示值为基准：目标 = 显示值 + 相对值（0 的轴不动）
            var targetX = dx != 0 ? s.XPosition + dx : -1;
            var targetY = dy != 0 ? s.YPosition + dy : -1;
            var targetZ = dz != 0 ? s.ZPosition + dz : -1;

            var zDirection = dz > 0 ? 1 : dz < 0 ? 2 : 0;

            return await MoveAsync(targetX, targetY, targetZ, targetX, targetY, targetZ, pollEncoder: false, zDirection, cancellationToken).ConfigureAwait(false);
        }
        //OperationCanceledException：表示"调用方主动取消了"，这不是一个失败结果，而是一个控制信号。它必须向上传播，让调用方（或 Task 调度层）知道这个任务是"被取消"而不是"执行失败"。
        catch (OperationCanceledException)  
        {
            throw;
        }
        catch (Exception ex)
        {
            return FailCommunication(ex, "相对移动");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken cancellationToken = default)
    {
        var status = await ReadRawStatusAsync(cancellationToken).ConfigureAwait(false);

        // 有板信号取 X11 线圈（现场确认 D5029 不是有板信号）。
        // 采集周期内比原先多一次线圈读；读失败按通信瞬时故障处理，由采集器标记断开并告警。
        var hasPlate = await _client.ReadCoilAsync(CraneAddress.X11_HasPlate, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>
        {
            ["X"] = (double)status.XPosition,
            ["Y"] = (double)status.YPosition,
            ["Z"] = (double)status.ZPosition,
            ["Alarm"] = status.ServoAlarm != 0 || status.PlcServoAlarm != 0 || status.PlcAlarm != 0,
            ["HasMaterial"] = hasPlate,
            ["XHome"] = status.XHomeCompleted,
            ["YHome"] = status.YHomeCompleted,
            ["ZHome"] = status.ZHomeCompleted
        };
    }

    /// <inheritdoc />
    public Task WriteSignalAsync(string pointName, object? value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("真实天车请使用具体操作方法（MoveAbsolute/Jog/MagnetOn 等），不支持按点名写入。");

    /// <summary>
    /// 进入手动模式：D4001=2（切手动）→ D4500=2（手动使能），首次写入并缓存。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task EnterManualAsync(CancellationToken cancellationToken = default)
    {
        if (_manualModeSet)
        {
            return;
        }

        await _client.WriteRegisterAsync(CraneAddress.ModeCmd, 2, cancellationToken).ConfigureAwait(false);
        await _client.WriteRegisterAsync(CraneAddress.ManualMode, 2, cancellationToken).ConfigureAwait(false);
        _manualModeSet = true;
        _logger.Info(LogCategory.Operation, $"天车[{_name}] 进入手动模式 D4001=2 D4500=2");
    }

    /// <summary>
    /// 设置三轴绝对速度/加速度/减速度（D2501~D2503 / D2510~D2512 / D2519~D2521）。
    /// </summary>
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
    public Task<CraneCommandResult> SetAbsoluteSpeedAsync(int speedX, int accelX, int decelX,
        int speedY, int accelY, int decelY, int speedZ, int accelZ, int decelZ,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("设置三轴绝对速度", async () =>
        {
            await _client.WriteRegisterAsync(CraneAddress.XAbsSpeed, (ushort)speedX, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.XAbsAccel, (ushort)accelX, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.XAbsDecel, (ushort)decelX, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.YAbsSpeed, (ushort)speedY, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.YAbsAccel, (ushort)accelY, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.YAbsDecel, (ushort)decelY, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.ZAbsSpeed, (ushort)speedZ, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.ZAbsAccel, (ushort)accelZ, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.ZAbsDecel, (ushort)decelZ, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>
    /// 执行绝对/相对运动：写目标 D3102~D3107 → 按 Z 方向脉冲触发 → 轮询到位。
    /// 传值 -1 表示该轴不参与。Z 下降时叠加 X2 下压保护。
    /// </summary>
    /// <param name="writeX">写入 X 目标的值（调用方按业务换算）</param>
    /// <param name="writeY">写入 Y 目标的值</param>
    /// <param name="writeZ">写入 Z 目标的值</param>
    /// <param name="targetX">X 到位目标</param>
    /// <param name="targetY">Y 到位目标</param>
    /// <param name="targetZ">Z 到位目标</param>
    /// <param name="pollEncoder">true=轮询绝对编码器、false=轮询显示值</param>
    /// <param name="zDirection">Z 方向（用于触发顺序）：1=下降 2=上升 0=不动</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>InPosition / PressureReached 为成功；Timeout / PressureOutOfRange 等为失败</returns>
    public async Task<CraneCommandResult> MoveAsync(int writeX, int writeY, int writeZ,
        int targetX, int targetY, int targetZ,
        bool pollEncoder, int zDirection, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);

            // 写目标（调用方按业务换算：相对运动写显示目标；绝对运动写 用户绝对值−编码器值+显示值）
            if (writeX != -1) await _client.WriteInt32Async(CraneAddress.XAbsTarget, writeX, cancellationToken).ConfigureAwait(false);
            if (writeY != -1) await _client.WriteInt32Async(CraneAddress.YAbsTarget, writeY, cancellationToken).ConfigureAwait(false);
            if (writeZ != -1) await _client.WriteInt32Async(CraneAddress.ZAbsTarget, writeZ, cancellationToken).ConfigureAwait(false);

            _logger.Info(LogCategory.Operation,
                $"天车[{_name}] 运动 写入 X={writeX} Y={writeY} Z={writeZ}；到位目标 X={targetX} Y={targetY} Z={targetZ}（轮询={(pollEncoder ? "编码器" : "显示值")}）");

            // 触发统一为"写 2 → 500ms → 写 0"，复位后再轮询到位；
            // 同一阶段的轴并行脉冲（一起写 2、一起写 0），不是写 2 后一直轮询再写 0。
            //注意 如果writex=-1 后面PulseManyAsync 不会写对应轴 2->延迟->0
            var xy = new[] { (CraneAddress.ManualXAbsMove, writeX != -1), (CraneAddress.ManualYAbsMove, writeY != -1) };
            var z = new[] { (CraneAddress.ManualZAbsMove, writeZ != -1) };

            if (zDirection == 1)
            {
                // Z 下降：先 XY（并行）后 Z    对应的轴 寄存器地址写2 延迟 写0
                await PulseManyAsync(xy, 500, cancellationToken).ConfigureAwait(false);
                var xyResult = await PollAxesAsync(targetX, targetY, -1, pollEncoder, false, cancellationToken).ConfigureAwait(false);
                if (!xyResult.IsSuccess) return xyResult;//这个就是xy没有运动成功
                await PulseManyAsync(z, 500, cancellationToken).ConfigureAwait(false);
            }
            else if (zDirection == 2)
            {
                // Z 上升：先 Z 后 XY（并行）
                await PulseManyAsync(z, 500, cancellationToken).ConfigureAwait(false);
                var zResult = await PollAxesAsync(-1, -1, targetZ, pollEncoder, false, cancellationToken).ConfigureAwait(false);
                if (!zResult.IsSuccess) return zResult;
                await PulseManyAsync(xy, 500, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Z 不变：三轴并行
                await PulseManyAsync(new[] { xy[0], xy[1], z[0] }, 500, cancellationToken).ConfigureAwait(false);
            }

            // Z 下降（zDirection=1）期间额外监视 X2 下压限位：压到即收尾并按下压容差判定成功/失败；其余方向不做下压保护。
            return await PollAxesAsync(targetX, targetY, targetZ, pollEncoder, zDirection == 1, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 运动自身超时以结果码返回（PollAxesAsync），能抛到这里的是连接/通信类异常
            return FailCommunication(ex, "运动");
        }
        finally
        {
            // 触发位必须复位；用不可取消令牌，避免取消/超时后卡在 2
            var resetCt = CancellationToken.None;
            if (writeX != -1) await TryWriteAsync(CraneAddress.ManualXAbsMove, 0, resetCt).ConfigureAwait(false);
            if (writeY != -1) await TryWriteAsync(CraneAddress.ManualYAbsMove, 0, resetCt).ConfigureAwait(false);
            if (writeZ != -1) await TryWriteAsync(CraneAddress.ManualZAbsMove, 0, resetCt).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// PLC 原生相对运动（点动）：D4501 距离 → D4502 方向 → D4505/04/03 触发。
    /// </summary>
    /// <param name="axis">轴：'X'/'Y'/'Z'</param>
    /// <param name="distance">距离（正数）</param>
    /// <param name="positive">方向：true 正向</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> JogAsync(char axis, int distance, bool positive, CancellationToken cancellationToken = default)
    {
        if (distance <= 0)
        {
            return Task.FromResult(CraneCommandResult.Fail(CraneCommandCode.Rejected, "距离必须大于 0"));
        }

        var upper = char.ToUpperInvariant(axis);
        if (upper is not ('X' or 'Y' or 'Z'))
        {
            return Task.FromResult(CraneCommandResult.Fail(CraneCommandCode.Rejected, $"未知轴 {axis}"));
        }

        return ExecuteAsync($"{upper} 点动", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.ManualDistance, (ushort)distance, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.ManualDirection, (ushort)(positive ? 1 : 2), cancellationToken).ConfigureAwait(false);

            var trigger = upper switch
            {
                'X' => CraneAddress.ManualMoveX,
                'Y' => CraneAddress.ManualMoveY,
                _ => CraneAddress.ManualMoveZ
            };
            await _client.WriteRegisterAsync(trigger, 2, cancellationToken).ConfigureAwait(false);
            _logger.Info(LogCategory.Operation, $"天车[{_name}] {upper}{(positive ? "+" : "-")} 点动 {distance}mm");
        });
    }

    /// <summary>单轴回原点（D4508/07/06=2 → 200ms → 0）。</summary>
    /// <param name="axis">轴：'X'/'Y'/'Z'</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> HomeAsync(char axis, CancellationToken cancellationToken = default)
    {
        var upper = char.ToUpperInvariant(axis);
        if (upper is not ('X' or 'Y' or 'Z'))
        {
            return Task.FromResult(CraneCommandResult.Fail(CraneCommandCode.Rejected, $"未知轴 {axis}"));
        }

        return ExecuteAsync($"{upper} 轴回原点", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            var reg = upper switch
            {
                'X' => CraneAddress.ManualHomeX,
                'Y' => CraneAddress.ManualHomeY,
                _ => CraneAddress.ManualHomeZ
            };
            await _client.WriteRegisterAsync(reg, 2, cancellationToken).ConfigureAwait(false);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(reg, 0, cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>充磁：D4510=2 → 轮询 X6=1 → 复位。最多 3 次、单次 3s。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> MagnetOnAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("充磁", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await PulseMagnetAsync(CraneAddress.ManualMagnetOn, CraneAddress.X6_MagnetizeOk, false, "充磁", cancellationToken).ConfigureAwait(false);
        }, feedbackTimeout: true);

    /// <summary>退磁：D4511=2 → 轮询 X7=1（且 X6=0）→ 复位。最多 3 次。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> MagnetOffAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("退磁", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await PulseMagnetAsync(CraneAddress.ManualMagnetOff, CraneAddress.X7_DemagnetizeOk, true, "退磁", cancellationToken).ConfigureAwait(false);
        }, feedbackTimeout: true);

    /// <summary>清除报警：D4514=2 → 200ms → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> ClearAlarmAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("清除报警", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.ManualClearAlarm, 2, cancellationToken).ConfigureAwait(false);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            await _client.WriteRegisterAsync(CraneAddress.ManualClearAlarm, 0, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>伺服通电：D4516=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> ServoOnAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("伺服通电", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await PulseAsync(CraneAddress.ManualServoPowerOn, true, 200, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>伺服断电：D4515=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> ServoOffAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("伺服断电", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await PulseAsync(CraneAddress.ManualServoPowerOff, true, 200, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>减速停止：D4517=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> SlowStopAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("减速停止", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await PulseAsync(CraneAddress.ManualSlowStop, true, 200, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>急停：D4518=2 → 200ms → 0（不切手动，紧急直写）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> EmergencyStopAsync(CancellationToken cancellationToken = default)
        // 急停与其余脉冲命令一样必须保证 200ms 脉冲宽度，否则 PLC 可能采不到这个脉冲。
        // 它与其它命令的唯一区别是"不切手动"——不走 D4001/D4500 前置，以便紧急情况下直接下发。
        => ExecuteAsync("急停", () => PulseAsync(CraneAddress.ManualEStop, true, 200, cancellationToken));

    /// <summary>开接液盘：D4512=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> DrainOpenAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("开接液盘", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await PulseAsync(CraneAddress.ManualDrainOpen, true, 200, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>关接液盘：D4513=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    public Task<CraneCommandResult> DrainCloseAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("关接液盘", async () =>
        {
            await EnterManualAsync(cancellationToken).ConfigureAwait(false);
            await PulseAsync(CraneAddress.ManualDrainClose, true, 200, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>
    /// 通用命令包装：执行命令体，成功返回 <see cref="CraneCommandCode.Completed"/>。
    /// 预期异常在此转成结果码、不抛给上层；取消异常（OperationCanceledException）继续抛出——取消是控制流，不是业务结果。
    /// </summary>
    /// <param name="label">动作名（日志/提示）</param>
    /// <param name="body">命令体</param>
    /// <param name="feedbackTimeout">true=超时视为反馈超时（充放磁）；false=超时按通信处理</param>
    /// <returns>结果</returns>
    private async Task<CraneCommandResult> ExecuteAsync(string label, Func<Task> body, bool feedbackTimeout = false)
    {
        try
        {
            await body().ConfigureAwait(false);
            _logger.Info(LogCategory.Operation, $"天车[{_name}] {label}");
            return CraneCommandResult.Ok(CraneCommandCode.Completed, $"{label} 完成");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            // 充放磁的 TimeoutException 来自反馈轮询；其余命令的 TimeoutException 来自连接超时，按通信处理
            var code = feedbackTimeout
                ? CraneCommandCode.FeedbackTimeout
                : _client.IsConnected ? CraneCommandCode.Communication : CraneCommandCode.NotConnected;
            return CraneCommandResult.Fail(code, $"天车[{_name}] {label}失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return CraneCommandResult.Fail(
                _client.IsConnected ? CraneCommandCode.Communication : CraneCommandCode.NotConnected,
                $"天车[{_name}] {label}失败：{ex.Message}");
        }
    }

    /// <summary>把通信类异常转成失败结果（已连接→Communication、未连接→NotConnected）。</summary>
    /// <param name="ex">异常</param>
    /// <param name="label">动作名</param>
    /// <returns>失败结果</returns>
    private CraneCommandResult FailCommunication(Exception ex, string label)
        => CraneCommandResult.Fail(
            _client.IsConnected ? CraneCommandCode.Communication : CraneCommandCode.NotConnected,
            $"天车[{_name}] {label}失败：{ex.Message}");

    /// <summary>写触发位 2 → 200ms → 0（脉冲）。</summary>
    /// <param name="register">寄存器地址</param>
    /// <param name="enabled">是否写（false 跳过）</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task PulseAsync(int register, bool enabled, int delayMs, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return;
        }

        await _client.WriteRegisterAsync(register, 2, cancellationToken).ConfigureAwait(false);
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        await _client.WriteRegisterAsync(register, 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 多寄存器并行脉冲：对全部启用寄存器一起写 2 → 延时 → 一起写 0。
    /// </summary>
    /// <param name="targets">(寄存器, 是否启用) 列表</param>
    /// <param name="delayMs">2 与 0 之间的间隔（毫秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task PulseManyAsync(IReadOnlyList<(int Register, bool Enabled)> targets, int delayMs, CancellationToken cancellationToken)
    {
        var active = new List<int>();
        foreach (var (register, enabled) in targets)
        {
            if (enabled)
            {
                active.Add(register);
            }
        }

        if (active.Count == 0)
        {
            return;
        }
        //寄存器地址写2
        foreach (var register in active)
        {
            await _client.WriteRegisterAsync(register, 2, cancellationToken).ConfigureAwait(false);
        }
        //固定延时
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        //寄存器地址写0
        foreach (var register in active)
        {
            await _client.WriteRegisterAsync(register, 0, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>磁铁脉冲：写 2 → 轮询反馈 → 复位；最多 3 次、单次 3s。</summary>
    /// <param name="trigger">触发寄存器</param>
    /// <param name="feedbackCoil">反馈线圈</param>
    /// <param name="requireX6Off">是否要求 X6=0（退磁）</param>
    /// <param name="label">动作名</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task PulseMagnetAsync(int trigger, int feedbackCoil, bool requireX6Off, string label, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        const int timeoutMs = 3000;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await _client.WriteRegisterAsync(trigger, 2, cancellationToken).ConfigureAwait(false);
            try
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    if (await _client.ReadCoilAsync(feedbackCoil, cancellationToken).ConfigureAwait(false))
                    {
                        if (requireX6Off && await _client.ReadCoilAsync(CraneAddress.X6_MagnetizeOk, cancellationToken).ConfigureAwait(false))
                        {
                            continue; // 退磁要求 X6=0
                        }

                        _logger.Info(LogCategory.Operation, $"天车[{_name}] {label}成功（第{attempt}次）");
                        return;
                    }
                }
            }
            finally
            {
                await _client.WriteRegisterAsync(trigger, 0, cancellationToken).ConfigureAwait(false);
            }

            _logger.Warn(LogCategory.Operation, $"天车[{_name}] {label}反馈未到位 第{attempt}/{maxAttempts} 次");
        }

        throw new TimeoutException($"天车[{_name}] {label}反馈未确认，已尝试 {maxAttempts} 次");
    }

    /// <summary>轮询指定轴到位（-1 表示跳过该轴）。不抛超时异常，超时以结果码返回。</summary>
    /// <param name="targetX">X 到位目标</param>
    /// <param name="targetY">Y 到位目标</param>
    /// <param name="targetZ">Z 到位目标</param>
    /// <param name="pollEncoder">true=读编码器、false=读显示值</param>
    /// <param name="watchPressure">true=每次轮询同时检查 X2 下压限位（Z 下降时用）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>InPosition / PressureReached 成功；Timeout / PressureOutOfRange 失败</returns>
    private async Task<CraneCommandResult> PollAxesAsync(int targetX, int targetY, int targetZ, bool pollEncoder, bool watchPressure, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_moveTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);

            var status = await ReadRawStatusAsync(cancellationToken).ConfigureAwait(false);
            var x = pollEncoder ? status.XEncoder : status.XPosition;
            var y = pollEncoder ? status.YEncoder : status.YPosition;
            var z = pollEncoder ? status.ZEncoder : status.ZPosition;

            _logger.Sample(LogCategory.Communication, $"{_name}-pos", $"天车[{_name}] 坐标 X={x} Y={y} Z={z}");

            // Z 下降叠加下压保护：压到 X2 即收尾（急停 + 清报警），再按 Z 与目标差是否在下压容差内判定成功/失败。
            if (watchPressure && await IsPressureLimitOnAsync(cancellationToken).ConfigureAwait(false))
            {
                await PressureStopAsync().ConfigureAwait(false);
                var delta = Math.Abs(z - targetZ);
                return delta <= _pressureTolerance
                    ? CraneCommandResult.Ok(CraneCommandCode.PressureReached,
                        $"天车[{_name}] 下压到位 Z={z} 目标={targetZ} 差={delta}mm（≤{_pressureTolerance}）")
                    : CraneCommandResult.Fail(CraneCommandCode.PressureOutOfRange,
                        $"天车[{_name}] 下压触发但未到目标 Z={z} 目标={targetZ} 差={delta}mm（>{_pressureTolerance}）");
            }

            var xOk = targetX == -1 || Math.Abs(x - targetX) <= _positionTolerance;
            var yOk = targetY == -1 || Math.Abs(y - targetY) <= _positionTolerance;
            var zOk = targetZ == -1 || Math.Abs(z - targetZ) <= _positionTolerance;
            //成功到位
            if (xOk && yOk && zOk)
            {
                _logger.Info(LogCategory.Operation, $"天车[{_name}] 到位 X={x} Y={y} Z={z}（容差±{_positionTolerance}）");
                return CraneCommandResult.Ok(CraneCommandCode.InPosition, $"天车[{_name}] 到位 X={x} Y={y} Z={z}");
            }
        }
        //到位失败  超时
        return CraneCommandResult.Fail(CraneCommandCode.Timeout,
            $"天车[{_name}] 运动超时未到位（目标 X={targetX} Y={targetY} Z={targetZ}，容差±{_positionTolerance}）");
    }

    /// <summary>
    /// 读 X2 下压限位。读失败返回 false（按"未触发"处理）并记日志——
    /// 这里与页面显示不同：运动保护里"读不到就当成已下压"会导致正常运动被误急停，
    /// 因此读失败时选择保守放行，由随后的状态读失败（通信中断）兜底。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>true=X2 已压下</returns>
    private async Task<bool> IsPressureLimitOnAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client.ReadCoilAsync(CraneAddress.X2_MagnetDownLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Communication, $"天车[{_name}] 下压保护读 X2 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 下压到位处理：急停 D4518=2 → 200ms → 0 → 使手动前置缓存失效 → 清除报警 D4514。
    /// 下压是正常工艺到位（不是报警/故障），但仍按急停 + 清报警流程收尾。
    /// 用不可取消令牌，保证该动作即使在被取消/超时时也能完整下发。
    /// </summary>
    private async Task PressureStopAsync()
    {
        var ct = CancellationToken.None;

        // 天车急停只有 D4518（见《手动操作页面.md》按钮表），下压到位也走它，不存在单独的下压急停寄存器
        await PulseAsync(CraneAddress.ManualEStop, true, 200, ct).ConfigureAwait(false);

        // 下压急停后手动使能失效，必须重写 D4001/D4500，否则后续命令不生效
        _manualModeSet = false;
        await EnterManualAsync(ct).ConfigureAwait(false);

        await _client.WriteRegisterAsync(CraneAddress.ManualClearAlarm, 2, ct).ConfigureAwait(false);
        await Task.Delay(200, ct).ConfigureAwait(false);
        await _client.WriteRegisterAsync(CraneAddress.ManualClearAlarm, 0, ct).ConfigureAwait(false);

        _logger.Info(LogCategory.Operation, $"天车[{_name}] 到达下压限位 X2：已急停（D4518）并清除报警（D4514），视为正常结束");
    }

    /// <summary>写寄存器，失败仅记录不抛出（用于 finally 复位）。</summary>
    /// <param name="register">地址</param>
    /// <param name="value">值</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task TryWriteAsync(int register, ushort value, CancellationToken cancellationToken)
    {
        try
        {
            await _client.WriteRegisterAsync(register, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Communication, $"天车[{_name}] 复位 D{register}=0 失败：{ex.Message}");
        }
    }

    /// <summary>组合 DINT 小端（低字在前）。</summary>
    /// <param name="low">低字</param>
    /// <param name="high">高字</param>
    /// <returns>32 位值</returns>
    private static int Combine(ushort low, ushort high) => unchecked((int)(((uint)high << 16) | low));
}
