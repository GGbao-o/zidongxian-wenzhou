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

        try
        {
            var s = await crane.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
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
                HasPlate = s.HasRoller,
                MagnetizeFeedback = await crane.ReadCoilAsync(CraneAddress.X6_MagnetizeOk, cancellationToken).ConfigureAwait(false),
                DemagnetizeFeedback = await crane.ReadCoilAsync(CraneAddress.X7_DemagnetizeOk, cancellationToken).ConfigureAwait(false),
                PressureLimit = await crane.ReadCoilAsync(CraneAddress.X2_MagnetDownLimit, cancellationToken).ConfigureAwait(false),
                DrainOpen = await crane.ReadCoilAsync(CraneAddress.X0_DrainOpen, cancellationToken).ConfigureAwait(false),
                DrainClose = await crane.ReadCoilAsync(CraneAddress.X1_DrainClose, cancellationToken).ConfigureAwait(false),
                PhysicalHomeX = await crane.ReadCoilAsync(CraneAddress.X3_HomeX, cancellationToken).ConfigureAwait(false),
                PhysicalHomeY = await crane.ReadCoilAsync(CraneAddress.X4_HomeY, cancellationToken).ConfigureAwait(false),
                PhysicalHomeZ = await crane.ReadCoilAsync(CraneAddress.X5_HomeZ, cancellationToken).ConfigureAwait(false)
            };
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Communication, $"手动页读取天车[{craneId}] 状态失败：{ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public Task<ManualOperationResult> SetSpeedAsync(int craneId, CraneSpeedDto speed, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.SetAbsoluteSpeedAsync(
            speed.SpeedX, speed.AccelX, speed.DecelX,
            speed.SpeedY, speed.AccelY, speed.DecelY,
            speed.SpeedZ, speed.AccelZ, speed.DecelZ,
            cancellationToken), "设置三轴绝对速度", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> MoveAbsoluteAsync(int craneId, int x, int y, int z, CancellationToken cancellationToken = default)
        => RunStatusAsync(craneId, async crane =>
        {
            var s = await crane.ReadStatusAsync(cancellationToken).ConfigureAwait(false);

            // 写入 D3102~D3107 = 用户绝对值 − 当前编码器值 + 当前显示值；到位轮询绝对编码器
            var writeX = x - (s.XEncoder - s.XPosition);
            var writeY = y - (s.YEncoder - s.YPosition);
            var writeZ = z - (s.ZEncoder - s.ZPosition);

            var dz = z - s.ZEncoder;
            var zDirection = dz > Tolerance ? 1 : dz < -Tolerance ? 2 : 0;

            await crane.MoveAsync(writeX, writeY, writeZ, x, y, z, pollEncoder: true, zDirection, Tolerance, cancellationToken)
                .ConfigureAwait(false);
        }, "绝对移动", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> MoveRelativeAsync(int craneId, int dx, int dy, int dz, CancellationToken cancellationToken = default)
        => RunStatusAsync(craneId, async crane =>
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

            await crane.MoveAsync(targetX, targetY, targetZ, targetX, targetY, targetZ, pollEncoder: false, zDirection, Tolerance, cancellationToken)
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

    /// <summary>需要先读状态再执行的命令。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="action">动作</param>
    /// <param name="label">动作名</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    private Task<ManualOperationResult> RunStatusAsync(int craneId, Func<CraneService, Task> action, string label, CancellationToken cancellationToken)
        => RunAsync(craneId, action, label, cancellationToken);

    /// <summary>解析天车真实服务。</summary>
    /// <param name="craneId">天车号</param>
    /// <returns>天车服务；未装载返回 null</returns>
    private CraneService? Resolve(int craneId)
        => _driverRegistry.GetCrane(craneId) as CraneService;
}
