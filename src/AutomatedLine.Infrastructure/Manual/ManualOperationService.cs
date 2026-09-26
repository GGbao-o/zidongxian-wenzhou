using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Infrastructure.Manual;

/// <summary>
/// 手动操作服务实现（基于真实汇川天车驱动）。
/// 通过 <see cref="DriverRegistry"/> 取到对应天车的 <see cref="ICraneDriver"/>，再执行读取与下发。
/// 与自动线路状态机**共用同一个 <see cref="ICraneDriver"/> 契约**，命令/读取方法完全一致。
/// 所有命令写操作日志到 operation（手动页操作日志）。
/// </summary>
public sealed class ManualOperationService : IManualOperationService
{
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

        // 读取逻辑已下沉到驱动（含 X 区逐点容错与失败日志），这里只做"未装载返回 null"的兜底。
        return await crane.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
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
        => RunAsync(craneId, crane => crane.MoveAbsoluteAsync(x, y, z, cancellationToken), "绝对移动", cancellationToken);

    /// <inheritdoc />
    public Task<ManualOperationResult> MoveRelativeAsync(int craneId, int dx, int dy, int dz, CancellationToken cancellationToken = default)
        => RunAsync(craneId, crane => crane.MoveRelativeAsync(dx, dy, dz, cancellationToken), "相对移动", cancellationToken);

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

    /// <summary>
    /// 通用命令执行：解析天车、执行、把 <see cref="CraneCommandResult"/> 映射为页面结果。
    /// 结果对象已表达预期内的成败（到位/下压到位/超时/下压超差等），这里不再依赖异常做业务判断。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="action">动作</param>
    /// <param name="label">动作名（日志/提示）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结果</returns>
    private async Task<ManualOperationResult> RunAsync(int craneId, Func<ICraneDriver, Task<CraneCommandResult>> action, string label, CancellationToken cancellationToken)
    {
        var crane = Resolve(craneId);
        if (crane is null)
        {
            return ManualOperationResult.Fail($"天车[{craneId}] 未装载驱动，无法操作");
        }

        try
        {
            var result = await action(crane).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _logger.Info(LogCategory.Operation, $"手动操作 天车[{craneId}] {label} 成功（{result.Code}）：{result.Message}");
                return ManualOperationResult.Ok(result.Message);
            }

            _logger.Warn(LogCategory.Operation, $"手动操作 天车[{craneId}] {label} 失败（{result.Code}）：{result.Message}");
            return ManualOperationResult.Fail(result.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.Operation, $"手动操作 天车[{craneId}] {label} 异常：{ex.Message}");
            return ManualOperationResult.Fail($"{label} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析天车驱动。返回 <see cref="ICraneDriver"/>（与自动引擎同一个契约），
    /// 不再向下强转成具体实现，保证手动页与状态机共用同一套方法。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <returns>天车驱动；未装载返回 null</returns>
    private ICraneDriver? Resolve(int craneId)
        => _driverRegistry.GetCrane(craneId);
}
