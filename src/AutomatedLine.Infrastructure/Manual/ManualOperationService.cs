using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Infrastructure.Communication;

namespace AutomatedLine.Infrastructure.Manual;

/// <summary>
/// 手动操作服务实现（基于真实汇川天车服务）。
/// 通过 <see cref="DriverRegistry"/> 取到对应天车的 <see cref="CraneService"/>，再执行读取与下发。
/// 所有命令写操作日志到 operation（手动页操作日志）。
/// </summary>
public sealed class ManualOperationService : IManualOperationService
{
    /// <summary>到位容差（mm）。</summary>
    private const int Tolerance = 5;

    /// <summary>驱动注册表。</summary>
    private readonly DriverRegistry _driverRegistry;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="driverRegistry">驱动注册表</param>
    /// <param name="logger">日志</param>
    public ManualOperationService(DriverRegistry driverRegistry, IAppLogger logger)
    {
        _driverRegistry = driverRegistry;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CraneStatusDto?> ReadStatusAsync(int craneId, CancellationToken cancellationToken = default)
    {
        var crane = Resolve(craneId);
        if (crane is null)
        {
            return null;
        }

        // 第一段：批量读 D5000~D5029 与速度区。
        // 这一段拿不到就没有坐标可显示，返回 null 让页面提示"连接失败"，并记录一条日志。
        CraneStatus s;
        try
        {
            s = await crane.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Communication,
                $"手动页读取天车[{craneId}] 状态失败（D5000~D5029 批量读 + 速度区 D2501~D2521）：{ex.Message}");
            return null;
        }

        // 第二段：X 区线圈逐个读。任一点失败只把该点标记为"未知"（null），
        // 不再让整个状态区一起消失——坐标、编码器、报警仍可正常显示。
        // 失败原因按点名写日志，便于区分到底哪个点读不到，而不是笼统一句"读取失败"。
        var dto = new CraneStatusDto
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
            HasPlate = await ReadCoilAsync(crane, craneId, CraneAddress.X11_HasPlate, "X11 检测有版", cancellationToken).ConfigureAwait(false),
            MagnetizeFeedback = await ReadCoilAsync(crane, craneId, CraneAddress.X6_MagnetizeOk, "X6 充磁反馈", cancellationToken).ConfigureAwait(false),
            DemagnetizeFeedback = await ReadCoilAsync(crane, craneId, CraneAddress.X7_DemagnetizeOk, "X7 退磁反馈", cancellationToken).ConfigureAwait(false),
            PressureLimit = await ReadCoilAsync(crane, craneId, CraneAddress.X2_MagnetDownLimit, "X2 磁铁下压限位", cancellationToken).ConfigureAwait(false),
            DrainOpen = await ReadCoilAsync(crane, craneId, CraneAddress.X0_DrainOpen, "X0 接液盘门开到位", cancellationToken).ConfigureAwait(false),
            DrainClose = await ReadCoilAsync(crane, craneId, CraneAddress.X1_DrainClose, "X1 接液盘门关到位", cancellationToken).ConfigureAwait(false),
            PhysicalHomeX = await ReadCoilAsync(crane, craneId, CraneAddress.X3_HomeX, "X3 X 轴物理原点", cancellationToken).ConfigureAwait(false),
            PhysicalHomeY = await ReadCoilAsync(crane, craneId, CraneAddress.X4_HomeY, "X4 Y 轴物理原点", cancellationToken).ConfigureAwait(false),
            PhysicalHomeZ = await ReadCoilAsync(crane, craneId, CraneAddress.X5_HomeZ, "X5 Z 轴物理原点", cancellationToken).ConfigureAwait(false)
        };

        return dto;
    }

    /// <summary>
    /// 读单个 X 区线圈。
    /// 读取失败返回 null（未知）并记录一条含点名的日志，**绝不按 0 处理**——
    /// 否则通信故障会被误读成"无板 / 未下压"，进而做出错误的现场判断。
    /// </summary>
    /// <param name="crane">天车服务</param>
    /// <param name="craneId">天车号（日志用）</param>
    /// <param name="address">线圈地址</param>
    /// <param name="name">点位名称（日志用）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>true/false 为实际电平；null 表示该点读取失败</returns>
    private async Task<bool?> ReadCoilAsync(CraneService crane, int craneId, int address, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await crane.ReadCoilAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Communication,
                $"手动页读取天车[{craneId}] {name}（线圈 {address}）失败：{ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />设置三轴速度
    public Task<ManualOperationResult> SetSpeedAsync(int craneId, CraneSpeedDto speed, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.SetAbsoluteSpeedAsync(
            speed.SpeedX, speed.AccelX, speed.DecelX,
            speed.SpeedY, speed.AccelY, speed.DecelY,
            speed.SpeedZ, speed.AccelZ, speed.DecelZ,
            cancellationToken), "设置三轴绝对速度", cancellationToken);

    /// <inheritdoc /> 绝对移动  参数  天车id  绝对值 xyz
    public Task<ManualOperationResult> MoveAbsoluteAsync(int craneId, int x, int y, int z, CancellationToken cancellationToken = default)
        => RunMoveAsync(craneId, async crane =>
        {
            var s = await crane.ReadStatusAsync(cancellationToken).ConfigureAwait(false);

            // 写入 D3102~D3107 = 用户绝对值 − 当前编码器值 + 当前显示值；到位轮询绝对编码器
            var writeX = x - (s.XEncoder - s.XPosition);
            var writeY = y - (s.YEncoder - s.YPosition);
            var writeZ = z - (s.ZEncoder - s.ZPosition);

            var dz = z - s.ZEncoder;
            var zDirection = dz > Tolerance ? 1 : dz < -Tolerance ? 2 : 0;

            return await crane.MoveAsync(writeX, writeY, writeZ, x, y, z, pollEncoder: true, zDirection, Tolerance, cancellationToken)
                .ConfigureAwait(false);
        }, "绝对移动", cancellationToken);

    /// <inheritdoc />  相对移动
    public Task<ManualOperationResult> MoveRelativeAsync(int craneId, int dx, int dy, int dz, CancellationToken cancellationToken = default)
        => RunMoveAsync(craneId, async crane =>
        {
            if (dx == 0 && dy == 0 && dz == 0)
            {
                throw new InvalidOperationException("相对值不能全为 0");
            }

            var s = await crane.ReadStatusAsync(cancellationToken).ConfigureAwait(false);

            // 以显示值为基准：目标 = 显示值 + 相对值（0 的轴不动）
            var targetX = dx != 0 ? s.XPosition + dx : -1;
            var targetY = dy != 0 ? s.YPosition + dy : -1;
            var targetZ = dz != 0 ? s.ZPosition + dz : -1;

            var zDirection = dz > 0 ? 1 : dz < 0 ? 2 : 0;

            return await crane.MoveAsync(targetX, targetY, targetZ, targetX, targetY, targetZ, pollEncoder: false, zDirection, Tolerance, cancellationToken)
                .ConfigureAwait(false);
        }, "相对移动", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> ClearAlarmAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.ClearAlarmAsync(cancellationToken), "清除报警", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> ServoOnAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.ServoOnAsync(cancellationToken), "伺服通电", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> ServoOffAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.ServoOffAsync(cancellationToken), "伺服断电", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> SlowStopAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.SlowStopAsync(cancellationToken), "减速停止", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> EmergencyStopAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.EmergencyStopAsync(cancellationToken), "急停", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> HomeAsync(int craneId, char axis, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.HomeAsync(axis, cancellationToken), $"{char.ToUpperInvariant(axis)} 轴回原点", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> MagnetOnAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.MagnetOnAsync(cancellationToken), "充磁", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> MagnetOffAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.MagnetOffAsync(cancellationToken), "放磁", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> DrainOpenAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.DrainOpenAsync(cancellationToken), "打开接液盘", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> DrainCloseAsync(int craneId, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.DrainCloseAsync(cancellationToken), "关闭接液盘", cancellationToken);

    /// <summary>通用命令执行：解析天车、执行、写操作日志、异常转失败结果。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="action">动作</param>
    /// <param name="label">动作名（日志/提示）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    private async Task<ManualOperationResult> RunAsync(int craneId, Func<CraneService, Task> action, string label, CancellationToken cancellationToken)
    {
        var crane = Resolve(craneId);
        if (crane is null)
        {
            return ManualOperationResult.Fail($"天车[{craneId}] 未装载驱动，无法操作");
        }

        try
        {
            await action(crane).ConfigureAwait(false);
            _logger.Info(LogCategory.Operation, $"手动操作 天车[{craneId}] {label}");
            return ManualOperationResult.Ok($"{label} 已下发");
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Operation, $"手动操作 天车[{craneId}] {label} 失败：{ex.Message}");
            return ManualOperationResult.Fail($"{label} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 执行移动命令：把"压到下压限位"与"到达目标"都视为**成功**返回。
    /// 下压是正常工艺到位，驱动已在返回 true 前完成下压急停与清报警。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="action">动作，返回 true 表示因下压限位结束</param>
    /// <param name="label">动作名（日志/提示）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    private async Task<ManualOperationResult> RunMoveAsync(int craneId, Func<CraneService, Task<bool>> action, string label, CancellationToken cancellationToken)
    {
        var crane = Resolve(craneId);
        if (crane is null)
        {
            return ManualOperationResult.Fail($"天车[{craneId}] 未装载驱动，无法操作");
        }

        try
        {
            //是否是下压信号量了返回
            var pressureStopped = await action(crane).ConfigureAwait(false);
            if (pressureStopped)
            {
                _logger.Info(LogCategory.Operation, $"手动操作 天车[{craneId}] {label} 到达下压限位，视为到位");
                return ManualOperationResult.Ok($"{label} 到达下压限位");
            }

            _logger.Info(LogCategory.Operation, $"手动操作 天车[{craneId}] {label} 到位");
            return ManualOperationResult.Ok($"{label} 到位");
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Operation, $"手动操作 天车[{craneId}] {label} 失败：{ex.Message}");
            return ManualOperationResult.Fail($"{label} 失败：{ex.Message}");
        }
    }

    /// <summary>解析天车真实服务。</summary>
    /// <param name="craneId">天车号</param>
    /// <returns>天车服务；未装载返回 null</returns>
    private CraneService? Resolve(int craneId)
        => _driverRegistry.GetCrane(craneId) as CraneService;
}
