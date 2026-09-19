using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Application.Manual;

/// <summary>
/// 手动操作服务实现。
/// 通过驱动按逻辑点名写天车 PLC 数据；不做与自动线的互斥与排队（现场约定：自动运行时人为不手动操作）。
/// 所有手动动作都会写操作日志，便于事后追溯是谁在什么时候动了哪台天车。
/// </summary>
public sealed class ManualOperationService : IManualOperationService
{
    /// <summary>驱动注册表。</summary>
    private readonly DriverRegistry _driverRegistry;

    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="driverRegistry">驱动注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="logger">日志</param>
    public ManualOperationService(DriverRegistry driverRegistry, CraneRegistry craneRegistry, IAppLogger logger)
    {
        _driverRegistry = driverRegistry;
        _craneRegistry = craneRegistry;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> MoveAbsoluteAsync(int craneId, double x, double y, double z, double speed, CancellationToken cancellationToken = default)
        => SendAsync(craneId, $"绝对运动→({x:0.###},{y:0.###},{z:0.###}) 速度{speed:0.###}", async driver =>
        {
            await driver.WriteSignalAsync("MoveMode", 0, cancellationToken).ConfigureAwait(false);
            await driver.WriteSignalAsync("MoveSpeed", speed, cancellationToken).ConfigureAwait(false);
            await driver.WriteSignalAsync("TargetX", x, cancellationToken).ConfigureAwait(false);
            await driver.WriteSignalAsync("TargetY", y, cancellationToken).ConfigureAwait(false);
            await driver.WriteSignalAsync("TargetZ", z, cancellationToken).ConfigureAwait(false);
            await PulseMoveStartAsync(driver, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> MoveRelativeAsync(int craneId, CraneAxis axis, double delta, double speed, CancellationToken cancellationToken = default)
    {
        // 相对运动由上位机换算为绝对目标：读取当前坐标 + 位移
        var crane = _craneRegistry.Get(craneId);
        var signals = crane?.Signals;
        var x = signals?.GetDouble("X") ?? 0;
        var y = signals?.GetDouble("Y") ?? 0;
        var z = signals?.GetDouble("Z") ?? 0;

        switch (axis)
        {
            case CraneAxis.X:
                x += delta;
                break;
            case CraneAxis.Y:
                y += delta;
                break;
            case CraneAxis.Z:
                z += delta;
                break;
        }

        return await MoveAbsoluteAsync(craneId, x, y, z, speed, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> SetSpeedAsync(int craneId, double speed, CancellationToken cancellationToken = default)
        => SendAsync(craneId, $"设置速度={speed:0.###}", driver =>
            driver.WriteSignalAsync("MoveSpeed", speed, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> ClearAlarmAsync(int craneId, CancellationToken cancellationToken = default)
        => SendAsync(craneId, "清除报警", async driver =>
        {
            await driver.WriteSignalAsync("ClearAlarm", true, cancellationToken).ConfigureAwait(false);
            await driver.WriteSignalAsync("ClearAlarm", false, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> MagnetizeAsync(int craneId, CancellationToken cancellationToken = default)
        => SendAsync(craneId, "充磁", driver => driver.WriteSignalAsync("Magnetize", true, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> DemagnetizeAsync(int craneId, CancellationToken cancellationToken = default)
        => SendAsync(craneId, "放磁", driver => driver.WriteSignalAsync("Demagnetize", true, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<bool> PressDownAsync(int craneId, CancellationToken cancellationToken = default)
        => SendAsync(craneId, "下压", driver => driver.WriteSignalAsync("PressDown", true, cancellationToken), cancellationToken);

    /// <summary>
    /// 下发一次"启动运动"脉冲（置真再复位），避免 PLC 粘住命令。
    /// </summary>
    /// <param name="driver">驱动</param>
    /// <param name="cancellationToken">取消令牌</param>
    private static async Task PulseMoveStartAsync(IPlcDriver driver, CancellationToken cancellationToken)
    {
        // 置真再复位，形成一次脉冲，避免 PLC 粘住"启动"命令
        await driver.WriteSignalAsync("MoveStart", true, cancellationToken).ConfigureAwait(false);
        await driver.WriteSignalAsync("MoveStart", false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 统一的下发封装：解析天车与驱动，执行写动作并记录操作日志。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="action">动作描述（用于日志）</param>
    /// <param name="send">实际写驱动的委托</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>下发成功返回 true</returns>
    private async Task<bool> SendAsync(int craneId, string action, Func<IPlcDriver, Task> send, CancellationToken cancellationToken)
    {
        var crane = _craneRegistry.Get(craneId);
        if (crane is null)
        {
            _logger.Warn(LogCategory.Operation, $"手动操作失败：天车{craneId} 不存在");
            return false;
        }

        var driver = _driverRegistry.Get(craneId);
        if (driver is null)
        {
            _logger.Warn(LogCategory.Operation, $"手动操作失败：天车{craneId} 无驱动");
            return false;
        }

        await send(driver).ConfigureAwait(false);
        _logger.Info(LogCategory.Operation, $"手动操作 天车{craneId} {action}");
        return true;
    }
}
