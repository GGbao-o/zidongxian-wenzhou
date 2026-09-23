using AutomatedLine.Application.Abstractions;
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

    /// <summary>到位容差默认值（mm）。</summary>
    private const int DefaultTolerance = 5;

    /// <summary>运动超时兜底值（毫秒）：配置非法时使用 60s。</summary>
    private const int FallbackMoveTimeoutMs = 60_000;

    /// <summary>底层 Modbus 客户端。</summary>
    private readonly ModbusTcpClient _client;

    /// <summary>天车名称（日志用）。</summary>
    private readonly string _name;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>是否已确认处于手动模式（缓存，避免重复写 D4001/D4500）。</summary>
    private bool _manualModeSet;

    /// <summary>运动到位超时（毫秒，来自天车配置）。</summary>
    private readonly int _moveTimeoutMs;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="name">天车名称（日志用）</param>
    /// <param name="ip">PLC IP（来自数据库）</param>
    /// <param name="logger">日志</param>
    /// <param name="moveTimeoutMs">运动到位超时（毫秒），来自数据库配置；非法时用 60s</param>
    public CraneService(int craneId, string name, string ip, IAppLogger logger, int moveTimeoutMs = FallbackMoveTimeoutMs)
    {
        CraneId = craneId;
        _name = name;
        _logger = logger;
        _moveTimeoutMs = moveTimeoutMs > 0 ? moveTimeoutMs : FallbackMoveTimeoutMs;
        _client = new ModbusTcpClient(ip, PlcPort, UnitId);
        _logger.Info(LogCategory.System, $"天车[{name}] 创建服务，IP={ip}:{PlcPort}（端口/协议固定），运动超时={_moveTimeoutMs}ms");
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

    /// <summary>读取状态快照（D5000~D5029）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>状态快照</returns>
    public async Task<CraneStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
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
    public async Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken cancellationToken = default)
    {
        var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);

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
    public async Task SetAbsoluteSpeedAsync(int speedX, int accelX, int decelX,
        int speedY, int accelY, int decelY, int speedZ, int accelZ, int decelZ,
        CancellationToken cancellationToken = default)
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
        _logger.Info(LogCategory.Operation, $"天车[{_name}] 设置三轴绝对速度");
    }

    /// <summary>
    /// 执行绝对运动：写目标 D3102~D3107 → 按 Z 方向脉冲触发 → 轮询到位。
    /// 传值 -1 表示该轴不参与。
    /// </summary>
    /// <param name="xValue">写入 X 目标的值（调用方按业务换算）</param>
    /// <param name="yValue">写入 Y 目标的值</param>
    /// <param name="zValue">写入 Z 目标的值</param>
    /// <param name="zDirection">Z 方向（用于触发顺序）：1=下降 2=上升 0=不动</param>
    /// <param name="pollEncoder">true=轮询绝对编码器、false=轮询显示值</param>
    /// <param name="tolerance">到位容差（mm）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>true=Z 下降时压到 X2 下压限位而停下（正常结束）；false=正常到位</returns>
    public async Task<bool> MoveAsync(int writeX, int writeY, int writeZ,
        int targetX, int targetY, int targetZ,
        bool pollEncoder, int zDirection,
        int tolerance = DefaultTolerance, CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);

        // 写目标（调用方按业务换算：相对运动写显示目标；绝对运动写 用户绝对值−编码器值+显示值）
        if (writeX != -1) await _client.WriteInt32Async(CraneAddress.XAbsTarget, writeX, cancellationToken).ConfigureAwait(false);
        if (writeY != -1) await _client.WriteInt32Async(CraneAddress.YAbsTarget, writeY, cancellationToken).ConfigureAwait(false);
        if (writeZ != -1) await _client.WriteInt32Async(CraneAddress.ZAbsTarget, writeZ, cancellationToken).ConfigureAwait(false);

        _logger.Info(LogCategory.Operation,
            $"天车[{_name}] 绝对运动 写入 X={writeX} Y={writeY} Z={writeZ}；到位目标 X={targetX} Y={targetY} Z={targetZ}（轮询={(pollEncoder ? "编码器" : "显示值")}）");

        try
        {
            // 触发统一为“写 2 → 500ms → 写 0”，复位后再轮询到位；
            // 同一阶段的轴并行脉冲（一起写 2、一起写 0），不是写 2 后一直轮询再写 0。
            var xy = new[] { (CraneAddress.ManualXAbsMove, writeX != -1), (CraneAddress.ManualYAbsMove, writeY != -1) };
            var z = new[] { (CraneAddress.ManualZAbsMove, writeZ != -1) };

            if (zDirection == 1)
            {
                // Z 下降：先 XY（并行）后 Z
                await PulseManyAsync(xy, 500, cancellationToken).ConfigureAwait(false);
                await PollAxesAsync(targetX, targetY, -1, pollEncoder, tolerance, false, cancellationToken).ConfigureAwait(false);
                await PulseManyAsync(z, 500, cancellationToken).ConfigureAwait(false);
            }
            else if (zDirection == 2)
            {
                // Z 上升：先 Z 后 XY（并行）
                await PulseManyAsync(z, 500, cancellationToken).ConfigureAwait(false);
                await PollAxesAsync(-1, -1, targetZ, pollEncoder, tolerance, false, cancellationToken).ConfigureAwait(false);
                await PulseManyAsync(xy, 500, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Z 不变：三轴并行
                await PulseManyAsync(new[] { xy[0], xy[1], z[0] }, 500, cancellationToken).ConfigureAwait(false);
            }

            // Z 下降（zDirection=1）期间额外监视 X2 下压限位：压到即下压急停并视为正常结束（与到位同义）；
            // 其余方向不做下压保护，避免误报。
            return await PollAxesAsync(targetX, targetY, targetZ, pollEncoder, tolerance, zDirection == 1, cancellationToken).ConfigureAwait(false);
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
    public async Task JogAsync(char axis, int distance, bool positive, CancellationToken cancellationToken = default)
    {
        if (distance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distance), "距离必须大于 0");
        }

        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await _client.WriteRegisterAsync(CraneAddress.ManualDistance, (ushort)distance, cancellationToken).ConfigureAwait(false);
        await _client.WriteRegisterAsync(CraneAddress.ManualDirection, (ushort)(positive ? 1 : 2), cancellationToken).ConfigureAwait(false);

        var trigger = char.ToUpperInvariant(axis) switch
        {
            'X' => CraneAddress.ManualMoveX,
            'Y' => CraneAddress.ManualMoveY,
            'Z' => CraneAddress.ManualMoveZ,
            _ => throw new ArgumentException($"未知轴 {axis}", nameof(axis))
        };
        await _client.WriteRegisterAsync(trigger, 2, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"天车[{_name}] {char.ToUpperInvariant(axis)}{(positive ? "+" : "-")} 点动 {distance}mm");
    }

    /// <summary>单轴回原点（D4508/07/06=2 → 200ms → 0）。</summary>
    /// <param name="axis">轴：'X'/'Y'/'Z'</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task HomeAsync(char axis, CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        var reg = char.ToUpperInvariant(axis) switch
        {
            'X' => CraneAddress.ManualHomeX,
            'Y' => CraneAddress.ManualHomeY,
            'Z' => CraneAddress.ManualHomeZ,
            _ => throw new ArgumentException($"未知轴 {axis}", nameof(axis))
        };
        await _client.WriteRegisterAsync(reg, 2, cancellationToken).ConfigureAwait(false);
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        await _client.WriteRegisterAsync(reg, 0, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"天车[{_name}] {char.ToUpperInvariant(axis)} 轴回原点");
    }

    /// <summary>充磁：D4510=2 → 轮询 X6=1 → 复位。最多 3 次、单次 3s。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MagnetOnAsync(CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await PulseMagnetAsync(CraneAddress.ManualMagnetOn, CraneAddress.X6_MagnetizeOk, false, "充磁", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>退磁：D4511=2 → 轮询 X7=1（且 X6=0）→ 复位。最多 3 次。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MagnetOffAsync(CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await PulseMagnetAsync(CraneAddress.ManualMagnetOff, CraneAddress.X7_DemagnetizeOk, true, "退磁", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清除报警：D4514=2 → 200ms → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task ClearAlarmAsync(CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await _client.WriteRegisterAsync(CraneAddress.ManualClearAlarm, 2, cancellationToken).ConfigureAwait(false);
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        await _client.WriteRegisterAsync(CraneAddress.ManualClearAlarm, 0, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"天车[{_name}] 清除报警");
    }

    /// <summary>伺服通电：D4516=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task ServoOnAsync(CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await PulseAsync(CraneAddress.ManualServoPowerOn, true, 200, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"天车[{_name}] 伺服通电");
    }

    /// <summary>伺服断电：D4515=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task ServoOffAsync(CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await PulseAsync(CraneAddress.ManualServoPowerOff, true, 200, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"天车[{_name}] 伺服断电");
    }

    /// <summary>减速停止：D4517=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task SlowStopAsync(CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await PulseAsync(CraneAddress.ManualSlowStop, true, 200, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"天车[{_name}] 减速停止");
    }

    /// <summary>急停：D4518=2 → 200ms → 0（不切手动，紧急直写）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        // 急停与其余脉冲命令一样必须保证 200ms 脉冲宽度，否则 PLC 可能采不到这个脉冲。
        // 它与其它命令的唯一区别是"不切手动"——不走 D4001/D4500 前置，以便紧急情况下直接下发。
        await PulseAsync(CraneAddress.ManualEStop, true, 200, cancellationToken).ConfigureAwait(false);
        _logger.Warn(LogCategory.Operation, $"天车[{_name}] 急停 D4518=2→200ms→0");
    }

    /// <summary>开接液盘：D4512=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task DrainOpenAsync(CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await PulseAsync(CraneAddress.ManualDrainOpen, true, 200, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"天车[{_name}] 开接液盘");
    }

    /// <summary>关接液盘：D4513=2 → 0。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task DrainCloseAsync(CancellationToken cancellationToken = default)
    {
        await EnterManualAsync(cancellationToken).ConfigureAwait(false);
        await PulseAsync(CraneAddress.ManualDrainClose, true, 200, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"天车[{_name}] 关接液盘");
    }

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

        foreach (var register in active)
        {
            await _client.WriteRegisterAsync(register, 2, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

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

    /// <summary>轮询指定轴到位（-1 表示跳过该轴）。</summary>
    /// <param name="targetX">X 到位目标</param>
    /// <param name="targetY">Y 到位目标</param>
    /// <param name="targetZ">Z 到位目标</param>
    /// <param name="pollEncoder">true=读编码器、false=读显示值</param>
    /// <param name="tolerance">容差</param>
    /// <param name="watchPressure">true=每次轮询同时检查 X2 下压限位（Z 下降时用）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>true=压到 X2 下压限位而结束（正常结束，与到位同义）；false=正常到位</returns>
    private async Task<bool> PollAxesAsync(int targetX, int targetY, int targetZ, bool pollEncoder, int tolerance, bool watchPressure, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_moveTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);

            // 逐 200ms 检查下压限位：Z 下降压到 X2 即下压急停并正常结束（下压是正常工艺到位，不是故障）。
            if (watchPressure && await IsPressureLimitOnAsync(cancellationToken).ConfigureAwait(false))
            {
                await PressureStopAsync().ConfigureAwait(false);
                return true;
            }

            var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            var x = pollEncoder ? status.XEncoder : status.XPosition;
            var y = pollEncoder ? status.YEncoder : status.YPosition;
            var z = pollEncoder ? status.ZEncoder : status.ZPosition;

            _logger.Sample(LogCategory.Communication, $"{_name}-pos", $"天车[{_name}] 坐标 X={x} Y={y} Z={z}");

            var xOk = targetX == -1 || Math.Abs(x - targetX) <= tolerance;
            var yOk = targetY == -1 || Math.Abs(y - targetY) <= tolerance;
            var zOk = targetZ == -1 || Math.Abs(z - targetZ) <= tolerance;
            if (xOk && yOk && zOk)
            {
                _logger.Info(LogCategory.Operation, $"天车[{_name}] 到位 X={x} Y={y} Z={z}（容差±{tolerance}）");
                return false;
            }
        }

        throw new TimeoutException($"天车[{_name}] 运动超时未到位（目标 X={targetX} Y={targetY} Z={targetZ}，容差±{tolerance}）");
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
