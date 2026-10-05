using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 一次搬运流程的上下文（《应用层设计.md》§5.4）。
/// 提供给 handler 的原语：坐标换算（设备坐标现查数据库）、移动、等待信号、读写设备、充放磁。
/// </summary>
public sealed class TransferContext
{
    /// <summary>驱动注册表（取天车/设备驱动）。</summary>
    private readonly DriverRegistry _drivers;

    /// <summary>配置存储（现查设备 X/Y/Z）。</summary>
    private readonly IConfigurationStore _store;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>设备坐标缓存（本任务内只查一次）。</summary>
    private IReadOnlyDictionary<int, DeviceConfigDto>? _deviceConfigs;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="crane">执行天车</param>
    /// <param name="workpiece">工件任务</param>
    /// <param name="drivers">驱动注册表</param>
    /// <param name="store">配置存储</param>
    /// <param name="options">程序参数</param>
    /// <param name="logger">日志</param>
    public TransferContext(
        Crane crane,
        WorkpieceTask workpiece,
        DriverRegistry drivers,
        IConfigurationStore store,
        AppOptions options,
        IAppLogger logger)
    {
        Crane = crane;
        Workpiece = workpiece;
        _drivers = drivers;
        _store = store;
        Options = options;
        _logger = logger;
    }

    /// <summary>执行天车。</summary>
    public Crane Crane { get; }

    /// <summary>被搬运工件。</summary>
    public WorkpieceTask Workpiece { get; }

    /// <summary>程序参数（超时等）。</summary>
    public AppOptions Options { get; }

    /// <summary>已充磁（板可能已吸上天车）——不可回滚边界。</summary>
    public bool HasPieceOnCrane { get; set; }

    /// <summary>天车驱动。</summary>
    public ICraneDriver CraneDriver
        => _drivers.GetCrane(Crane.Id) ?? throw new InvalidOperationException($"天车 {Crane.Id} 无驱动");

    /// <summary>日志前缀：天车 + 工件。</summary>
    private string Tag => $"[{Crane.Name}] 工件 {Workpiece.Info}";

    /// <summary>按设备号取设备驱动。</summary>
    /// <param name="deviceId">设备号</param>
    /// <returns>设备驱动</returns>
    public IDeviceDriver DeviceDriver(int deviceId)
        => _drivers.GetDevice(deviceId) ?? throw new InvalidOperationException($"设备 {deviceId} 无驱动");

    /// <summary>取设备绝对基坐标（设备库 X/Y/Z + 本车偏移；坐标现查一次并缓存）。</summary>
    /// <param name="device">设备</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>(基坐标 X, 基坐标 Y, 基坐标 Z)</returns>
    public async Task<(double X, double Y, double Z)> BaseOfAsync(DeviceBase device, CancellationToken cancellationToken)
    {
        _deviceConfigs ??= (await _store.GetDeviceConfigsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Id);
        if (!_deviceConfigs.TryGetValue(device.Id, out var cfg))
        {
            throw new InvalidOperationException($"设备配置 {device.Id} 不存在");
        }

        //return的是加了天车偏移的
        return (
            CraneCoordinate.BaseX(cfg.PositionX, Crane),
            CraneCoordinate.BaseY(cfg.PositionY, Crane),
            CraneCoordinate.BaseZ(cfg.PositionZ, Crane));
    }

    /// <summary>绝对移动并校验成功（失败抛业务异常，走二分中止）。</summary>
    /// <param name="x">X（内部四舍五入取整）</param>
    /// <param name="y">Y（内部四舍五入取整）</param>
    /// <param name="z">Z（内部四舍五入取整）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MoveAbsoluteAsync(double x, double y, double z, CancellationToken cancellationToken)
    {
        var (ix, iy, iz) = ((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(z));
        _logger.Info(LogCategory.StateMachine, $"{Tag} 绝对运动 → X{ix} Y{iy} Z{iz}");
        var result = await CraneDriver.MoveAbsoluteAsync(ix, iy, iz, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{Tag} 绝对运动失败（目标 X{ix} Y{iy} Z{iz}）：{result.Code} {result.Message}");
        }
    }

    /// <summary>相对移动并校验成功。</summary>
    /// <param name="dx">X 相对值</param>
    /// <param name="dy">Y 相对值</param>
    /// <param name="dz">Z 相对值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MoveRelativeAsync(int dx, int dy, int dz, CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{Tag} 相对运动 → dX{dx} dY{dy} dZ{dz}");
        var result = await CraneDriver.MoveRelativeAsync(dx, dy, dz, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{Tag} 相对运动失败（dX{dx} dY{dy} dZ{dz}）：{result.Code} {result.Message}");
        }
    }

    /// <summary>下发本车三轴速度/加速度/减速度（流程开始时设置一次）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task SetCraneSpeedAsync(CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{Tag} 设置天车速度");
        var result = await CraneDriver.SetAbsoluteSpeedAsync(
            (int)Math.Round(Crane.SpeedX), (int)Math.Round(Crane.AccelX), (int)Math.Round(Crane.DecelX),
            (int)Math.Round(Crane.SpeedY), (int)Math.Round(Crane.AccelY), (int)Math.Round(Crane.DecelY),
            (int)Math.Round(Crane.SpeedZ), (int)Math.Round(Crane.AccelZ), (int)Math.Round(Crane.DecelZ),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"设置天车速度失败：{result.Code} {result.Message}");
        }
    }

    /// <summary>充磁（内部轮询 X6）并置不可回滚边界。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MagnetOnAsync(CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{Tag} 充磁");
        var result = await CraneDriver.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{Tag} 充磁失败：{result.Code} {result.Message}");
        }

        HasPieceOnCrane = true;   // 指令一发出即视为已吸起，之后异常不可回滚
    }

    /// <summary>放磁（内部轮询 X7）并校验成功。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task MagnetOffAsync(CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{Tag} 放磁");
        var result = await CraneDriver.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{Tag} 放磁失败：{result.Code} {result.Message}");
        }
    }

    /// <summary>轮询天车状态直到满足条件；超时/报警抛异常。</summary>
    /// <param name="predicate">条件（如 dto =&gt; dto.HasPlate == true）</param>
    /// <param name="description">条件描述（日志/异常）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WaitCraneAsync(Func<CraneStatusDto, bool> predicate, string description, CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{Tag} 等待天车 {description}");
        var timeout = TimeSpan.FromMilliseconds(Options.StateMachine.HandshakeTimeoutMs);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = await CraneDriver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (dto is not null && dto.HasAlarm)
            {
                throw new InvalidOperationException($"{Tag} 天车报警，等待 {description} 中止");
            }

            if (dto is not null && predicate(dto))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"{Tag} 等待天车 {description} 超时（{timeout.TotalMilliseconds}ms）");
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>轮询设备输入信号直到等于期望值；超时抛异常。</summary>
    /// <param name="device">目标设备</param>
    /// <param name="signal">点名</param>
    /// <param name="expected">期望布尔值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WaitDeviceAsync(DeviceBase device, string signal, bool expected, CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{Tag} 等待 {device.Name}.{signal}={expected}");
        var timeout = TimeSpan.FromMilliseconds(Options.StateMachine.HandshakeTimeoutMs);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 读采集器写入的**缓存快照**（不实时读 PLC，避免与 DeviceStateCollector 争连接）。
            // 信号必须真实读到：缺失视为"未知"，不算满足。
            // 否则 expected=false（如"下料架无板"）会把"没采到数据"误当成"无料"，可能往实际有板的架子放料。
            var snapshot = device.Signals;
            if (snapshot is not null && snapshot.Values.TryGetValue(signal, out var v))
            {
                var value = v is bool b && b;
                if (value == expected)
                {
                    return;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"{Tag} 等待 {device.Name}.{signal}={expected} 超时（{timeout.TotalMilliseconds}ms）");
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>写设备点位并校验（驱动不支持时抛异常）。</summary>
    /// <param name="device">设备</param>
    /// <param name="signal">点名</param>
    /// <param name="value">值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task WriteDeviceAsync(DeviceBase device, string signal, object? value, CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{Tag} 写 {device.Name}.{signal}={value}");
        return DeviceDriver(device.Id).WriteSignalAsync(signal, value, cancellationToken);
    }

    /// <summary>
    /// 写一条**流程步骤**日志：自动带 **天车 + 工件**，并标注**设备**与当前走到哪一步。
    /// 与各原语（运动/等待/读写）的日志配合，可在 `statemachine.txt` 里按"天车 / 工件 / 设备 / 步骤"
    /// 串起一次完整搬运，便于现场排查卡在哪一步。
    /// </summary>
    /// <param name="device">当前操作的设备</param>
    /// <param name="action">步骤说明（如"取料：Z 下降到取料位"）</param>
    public void LogStep(DeviceBase device, string action)
        => _logger.Info(LogCategory.StateMachine, $"{Tag} [{device.Name}] {action}");

    /// <summary>写一条**流程步骤**日志（不针对具体设备，如收尾阶段）。</summary>
    /// <param name="action">步骤说明</param>
    public void LogStep(string action)
        => _logger.Info(LogCategory.StateMachine, $"{Tag} {action}");

    /// <summary>
    /// 写一条**流程步骤警告**日志（同样自动带 天车 + 工件）——
    /// 用于"本步失败但仍会继续或回滚"的场景，保留 WARN 级别便于现场按级别筛选。
    /// </summary>
    /// <param name="action">步骤说明</param>
    public void LogStepWarn(string action)
        => _logger.Warn(LogCategory.StateMachine, $"{Tag} {action}");
}
