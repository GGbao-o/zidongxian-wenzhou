using System.Diagnostics;
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

    /// <summary>固定本次搬运标识，收尾清空天车任务后日志仍能关联原搬运。</summary>
    private readonly Guid? _transportTaskId;

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
        _transportTaskId = crane.Runtime.CurrentTask?.TaskId;
    }

    /// <summary>执行天车。</summary>
    public Crane Crane { get; }

    /// <summary>被搬运工件。</summary>
    public WorkpieceTask Workpiece { get; }

    /// <summary>程序参数（超时等）。</summary>
    public AppOptions Options { get; }

    /// <summary>充磁反馈已成功，用于记录取料动作的结果；异常分支以指令开始下发为界。</summary>
    public bool HasPieceOnCrane { get; set; }

    /// <summary>取料阶段已开始调用充磁；仅在 PickAsync 充磁前置位，此后失败弃板，放磁后不复位。</summary>
    public bool MagnetCommandStarted { get; set; }

    /// <summary>本次搬运已进入源设备取料交互；异常清握手时结合预约归属判断，不依赖易变化的工件引用。</summary>
    internal bool SourceInteractionStarted { get; set; }

    /// <summary>本次搬运已进入目标设备放料交互；目标尚未接收内存工件时也可能已经写过握手。</summary>
    internal bool TargetInteractionStarted { get; set; }

    /// <summary>天车驱动。</summary>
    public ICraneDriver CraneDriver
        => _drivers.GetCrane(Crane.Id) ?? throw new InvalidOperationException($"天车 {Crane.Id} 无驱动");

    /// <summary>原异常提示前缀，保留现有弹窗和错误消息格式。</summary>
    private string Tag => $"[{Crane.Name}] 工件 {Workpiece.Info}";

    /// <summary>日志额外关联天车号及两个独立任务标识，不改变业务异常文本。</summary>
    private string LogTag => $"[{Crane.Name}/Id={Crane.Id}] 工件 {Workpiece.Info}（搬运={_transportTaskId?.ToString("N") ?? "未关联"}，工件Id={Workpiece.Id:N}）";

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
        _logger.Info(LogCategory.StateMachine,
            $"{LogTag} [{device.Name}/Id={device.Id}] 坐标换算：设备库XYZ=({cfg.PositionX},{cfg.PositionY},{cfg.PositionZ})，" +
            $"天车偏移XYZ=({Crane.OffsetX},{Crane.OffsetY},{Crane.OffsetZ})，" +
            $"基坐标XYZ=({CraneCoordinate.BaseX(cfg.PositionX, Crane)},{CraneCoordinate.BaseY(cfg.PositionY, Crane)},{CraneCoordinate.BaseZ(cfg.PositionZ, Crane)})；" +
            $"设备类型={device.Type}，直径={Workpiece.Info.Diameter}，Z下压量={CraneCoordinate.ZDrop(device.Type, Workpiece.Info.Diameter)}，安全位X={Crane.SafeX} Z={Crane.SafeZ}");
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
    /// <returns>异步运动任务；记录原命令结果，失败继续抛出原业务异常。</returns>
    public async Task MoveAbsoluteAsync(double x, double y, double z, CancellationToken cancellationToken)
    {
        var (ix, iy, iz) = ((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(z));
        _logger.Info(LogCategory.StateMachine, $"{LogTag} 绝对运动下发：原始目标XYZ=({x},{y},{z})，取整目标XYZ=({ix},{iy},{iz})；等待驱动实时到位/下压判定结果");
        var started = Stopwatch.GetTimestamp();
        var result = await CraneDriver.MoveAbsoluteAsync(ix, iy, iz, cancellationToken).ConfigureAwait(false);
        LogCommandResult("绝对运动", result, started);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{Tag} 绝对运动失败（目标 X{ix} Y{iy} Z{iz}）：{result.Code} {result.Message}");
        }
    }

    /// <summary>源/目标上方读取一次绝对编码器，仅在配置最大距离内补偿超差XY轴一次；任一轴超限则整次不动，不追加精度复核。</summary>
    /// <param name="x">本次X绝对目标，与首次运动使用相同取整。 就是当前天车要去的位置 已经加过偏移量了 不需要管偏移量的事</param>
    /// <param name="y">本次Y绝对目标，保留斜床pickY等业务偏移。</param>
    /// <param name="cancellationToken">当前搬运取消令牌。</param>
    /// <returns>跳过微调或一次相对运动完成的任务；Z增量始终为0。</returns>
    /// <exception cref="InvalidOperationException">参数非法、读取失败、报警、任一轴超限或相对运动失败，由原搬运异常路径处理。</exception>
    public async Task RefineXyAsync(double x, double y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (ix, iy) = ((int)Math.Round(x), (int)Math.Round(y));
        var tolerance = Options.Crane.XyRefineToleranceMm;
        var settleMs = Options.Crane.XyRefineSettleMs;
        var maxX = Options.Crane.XyRefineMaxXCorrectionMm;
        var maxY = Options.Crane.XyRefineMaxYCorrectionMm;
        if (tolerance <= 0 || settleMs < 0 || maxX <= 0 || maxY <= 0 || maxX < tolerance || maxY < tolerance)
            throw new InvalidOperationException($"{Tag} XY微调参数非法：容差={tolerance}mm须大于0，读取前等待={settleMs}ms须非负；X最大距离={maxX}mm、Y最大距离={maxY}mm须大于0且不小于容差");

        LogStep($"XY微调：等待{settleMs}ms后读取一次绝对编码器，目标X={ix} Y={iy}，误差>{tolerance}mm才补发，最大距离X={maxX}mm Y={maxY}mm，任一轴超限整次不下发，最多1次，Z不参与");
        await Task.Delay(settleMs, cancellationToken).ConfigureAwait(false);
        var status = await CraneDriver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        // 现有状态接口可能将读取取消转换为null；优先保留搬运取消语义。
        cancellationToken.ThrowIfCancellationRequested();
        if (status is null) throw new InvalidOperationException($"{Tag} XY微调读取天车状态失败，未下发微调");
        if (status.HasAlarm) throw new InvalidOperationException($"{Tag} XY微调发现天车报警，未下发微调");
        //获取目标位置与当前编码器位置的差值
        var errorX = (long)ix - status.XEncoder;
        var errorY = (long)iy - status.YEncoder;
        var absErrorX = Math.Abs(errorX);
        var absErrorY = Math.Abs(errorY);
        // 先核对两轴上限再生成任何运动增量；超限不能截断成允许距离，也不能让另一轴先移动。
        // 用long比较，避免极端编码器差值在转int时溢出而掩盖真正的定位超限原因。
        if (absErrorX > maxX || absErrorY > maxY)
        {
            var exceededAxes = absErrorX > maxX ? (absErrorY > maxY ? "X、Y" : "X") : "Y";
            var detail = $"XY微调超限：超限轴={exceededAxes}，目标X={ix} Y={iy}，编码器X={status.XEncoder} Y={status.YEncoder}，" +
                         $"差值dX={errorX} dY={errorY}mm，绝对误差X={absErrorX} Y={absErrorY}mm，阈值={tolerance}mm，最大距离X={maxX} Y={maxY}mm；整次未下发微调，交由原搬运异常处理";
            _logger.Error(LogCategory.StateMachine, $"{LogTag} {detail}");
            throw new InvalidOperationException($"{Tag} {detail}");
        }
        // 两轴均在最大距离内后，仅为严格超过触发阈值的轴生成相对增量。
        var dx = absErrorX > tolerance ? checked((int)errorX) : 0;
        var dy = absErrorY > tolerance ? checked((int)errorY) : 0;
        LogStep($"XY微调检查：编码器X={status.XEncoder} Y={status.YEncoder}，目标X={ix} Y={iy}，绝对误差X={absErrorX} Y={absErrorY}mm，阈值={tolerance}mm，最大距离X={maxX} Y={maxY}mm；修正量dX={dx} dY={dy} dZ=0");
        //说明不需要微调
        if (dx == 0 && dy == 0)
        {
            LogStep("XY微调：误差未超过阈值，直接继续，不补发运动");
            return;
        }

        // 范围内仍只补发一次；编码器差值作为相对增量，复用现有显示坐标换算、触发、到位等待和异常复位，不追加复核。
        await MoveRelativeAsync(dx, dy, 0, cancellationToken).ConfigureAwait(false);
        LogStep("XY微调：1/1次相对运动成功，按原运动到位规则继续，不追加精度复核");
    }

    /// <summary>相对移动并校验成功。</summary>
    /// <param name="dx">X 相对值</param>
    /// <param name="dy">Y 相对值</param>
    /// <param name="dz">Z 相对值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步运动任务；日志不增加位置读取或运动指令。</returns>
    public async Task MoveRelativeAsync(int dx, int dy, int dz, CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{LogTag} 相对运动 → dX{dx} dY{dy} dZ{dz}");
        var started = Stopwatch.GetTimestamp();
        var result = await CraneDriver.MoveRelativeAsync(dx, dy, dz, cancellationToken).ConfigureAwait(false);
        LogCommandResult("相对运动", result, started);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{Tag} 相对运动失败（dX{dx} dY{dy} dZ{dz}）：{result.Code} {result.Message}");
        }
    }

    /// <summary>下发本车三轴速度/加速度/减速度（流程开始时设置一次）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步设置任务；记录取整参数和驱动返回结果。</returns>
    public async Task SetCraneSpeedAsync(CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine,
            $"{LogTag} 设置天车速度（速度/加速度/减速度，原配置→取整指令）：" +
            $"X=({Crane.SpeedX},{Crane.AccelX},{Crane.DecelX})→({(int)Math.Round(Crane.SpeedX)},{(int)Math.Round(Crane.AccelX)},{(int)Math.Round(Crane.DecelX)})，" +
            $"Y=({Crane.SpeedY},{Crane.AccelY},{Crane.DecelY})→({(int)Math.Round(Crane.SpeedY)},{(int)Math.Round(Crane.AccelY)},{(int)Math.Round(Crane.DecelY)})，" +
            $"Z=({Crane.SpeedZ},{Crane.AccelZ},{Crane.DecelZ})→({(int)Math.Round(Crane.SpeedZ)},{(int)Math.Round(Crane.AccelZ)},{(int)Math.Round(Crane.DecelZ)})；等待写入返回");
        var started = Stopwatch.GetTimestamp();
        var result = await CraneDriver.SetAbsoluteSpeedAsync(
            (int)Math.Round(Crane.SpeedX), (int)Math.Round(Crane.AccelX), (int)Math.Round(Crane.DecelX),
            (int)Math.Round(Crane.SpeedY), (int)Math.Round(Crane.AccelY), (int)Math.Round(Crane.DecelY),
            (int)Math.Round(Crane.SpeedZ), (int)Math.Round(Crane.AccelZ), (int)Math.Round(Crane.DecelZ),
            cancellationToken).ConfigureAwait(false);
        LogCommandResult("设置天车速度", result, started);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"设置天车速度失败：{result.Code} {result.Message}");
        }
    }

    /// <summary>充磁（500ms脉冲复位后按配置等待再单读X6）并校验成功；取料异常边界由PickAsync调用前标记。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步命令任务，失败抛异常</returns>
    public async Task MagnetOnAsync(CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{LogTag} 充磁下发：等待驱动完成触发脉冲、复位及本次配置延时后单读X6=1；取料充磁调用标志={MagnetCommandStarted}");
        var started = Stopwatch.GetTimestamp();
        var result = await CraneDriver.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        LogCommandResult("充磁", result, started);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{Tag} 充磁失败：{result.Code} {result.Message}");
        }

        HasPieceOnCrane = true;
    }

    /// <summary>放磁（500ms脉冲复位后按配置等待再单读X7）并校验成功。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步命令任务，失败抛异常</returns>
    public async Task MagnetOffAsync(CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{LogTag} 放磁下发：等待驱动完成触发脉冲、复位及本次配置延时后单读X7=1");
        var started = Stopwatch.GetTimestamp();
        var result = await CraneDriver.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        LogCommandResult("放磁", result, started);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{Tag} 放磁失败：{result.Code} {result.Message}");
        }
    }

    /// <summary>取料到位后等500ms专用读取X11；明确无板时按配置仅Z补降并重查，全程仍属充磁前。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任一次有板时完成；配置非法、次数耗尽、专用读取/运动失败或取消时抛异常。</returns>
    public async Task CheckPlateBeforeMagnetAsync(CancellationToken cancellationToken)
    {
        //从配置中拿下次数和下将mm值
        var retryCount = Options.StateMachine.PickPlateRetryCount;
        var stepMm = Options.StateMachine.PickPlateStepMm;
        var completedDrops = 0;
        long totalCommandDropMm = 0;
        var position = "当前位置未知（尚未读取）";
        var action = "参数检查";
        try
        {
            if (retryCount < 0 || stepMm <= 0)
                throw new InvalidOperationException($"取料补降配置不合法：追加次数={retryCount}（须>=0），单次下降={stepMm}mm（须>0）");

            _logger.Info(LogCategory.StateMachine,
                $"{LogTag} 取料有板检查开始：最多追加下降{retryCount}次，每次{stepMm}mm，最大累计指令下降{(long)retryCount * stepMm}mm；原位及每次补降后均等待500ms");
            while (true)
            {
                // 仅发布展示进度；X11判断和补降仍按原实时读取流程执行。
                PublishStep($"取料：第{(long)completedDrops + 1}次检查有板（补降{completedDrops}/{retryCount}次）");
                action = "等待信号稳定";
                _logger.Info(LogCategory.StateMachine,
                    $"{LogTag} 取料：检查第{(long)completedDrops + 1}次前等待500ms，已完成补降{completedDrops}/{retryCount}次，累计指令下降{totalCommandDropMm}mm");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                action = "专用读取X11";
                // 只有专用读取明确返回false才允许继续下降；通信失败不能解释成无板，也不读取缓存判定。
                var hasPlate = await CraneDriver.ReadHasPlateAsync(cancellationToken).ConfigureAwait(false);
                action = "读取诊断位置";
                position = await ReadPlateCheckPositionAsync(cancellationToken).ConfigureAwait(false);
                var progress = $"已完成补降{completedDrops}/{retryCount}次，累计指令下降{totalCommandDropMm}mm，{position}";
                _logger.Info(LogCategory.StateMachine,
                    $"{LogTag} 取料：检查第{(long)completedDrops + 1}次，X11={(hasPlate ? 1 : 0)}，{progress}");
                if (hasPlate)
                {
                    _logger.Info(LogCategory.StateMachine, $"{LogTag} 取料有板检查通过：X11=1，{progress}，下一步才开始充磁");
                    return;
                }
                if (completedDrops >= retryCount)
                {
                    action = "补降次数耗尽";
                    throw new InvalidOperationException($"{Tag} 取料有板检查失败：X11=0，{progress}，仍无板，尚未充磁");
                }

                action = $"第{completedDrops + 1}/{retryCount}次补降";
                _logger.Warn(LogCategory.StateMachine,
                    $"{LogTag} 取料：X11=0，准备{action}，仅Z相对下降{stepMm}mm，计划累计指令下降{totalCommandDropMm + stepMm}mm；下压前{position}");
                // 本系统Z正向为下降；只动Z保留源设备XY及斜床Y偏移，复用原运动超时/下压判定。
                await MoveRelativeAsync(0, 0, stepMm, cancellationToken).ConfigureAwait(false);
                completedDrops++;
                totalCommandDropMm += stepMm;
                // 累计的是成功执行的指令距离，不把到位容差或下压限位结果冒充实测下降量。
                _logger.Info(LogCategory.StateMachine,
                    $"{LogTag} 取料：补降运动完成，已完成补降{completedDrops}/{retryCount}次，本次指令下降{stepMm}mm，累计指令下降{totalCommandDropMm}mm；等待稳定后重读X11和实际位置");
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.Warn(LogCategory.StateMachine,
                $"{LogTag} 取料有板检查中止：{action}，执行令牌已取消={cancellationToken.IsCancellationRequested}，已完成补降{completedDrops}/{retryCount}次，累计指令下降{totalCommandDropMm}mm，最后读取的{position}，原因：{ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.StateMachine,
                $"{LogTag} 取料有板检查失败：{action}，已完成补降{completedDrops}/{retryCount}次，累计指令下降{totalCommandDropMm}mm，最后读取的{position}，尚未充磁，原因：{ex.Message}", ex);
            throw;
        }
    }

    // 位置只用于诊断：实时读数不可用时明确记未知，不让附加日志读取改变专用X11判据或捏造零坐标。
    private async Task<string> ReadPlateCheckPositionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await CraneDriver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (status is not null)
                return $"当前显示坐标X={status.XDisplay} Y={status.YDisplay} Z={status.ZDisplay}；编码器X={status.XEncoder} Y={status.YEncoder} Z={status.ZEncoder}";
            _logger.Warn(LogCategory.StateMachine, $"{LogTag} 取料位置日志：实时状态不可用，当前位置记未知，X11仍以专用读取为准");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory.StateMachine, $"{LogTag} 取料位置日志读取失败，当前位置记未知：{ex.Message}");
        }
        return "当前位置未知（实时状态不可用）";
    }

    /// <summary>轮询天车状态直到满足条件；超时/报警抛异常。</summary>
    /// <param name="predicate">条件（如 dto =&gt; dto.HasPlate == true）</param>
    /// <param name="description">条件描述（日志/异常）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步等待任务；只记录原有读取结果，保留原超时和报警判定。</returns>
    public async Task WaitCraneAsync(Func<CraneStatusDto, bool> predicate, string description, CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{LogTag} 等待天车 {description}：实时读取，超时={Options.StateMachine.HandshakeTimeoutMs}ms，轮询等待=200ms");
        var timeout = TimeSpan.FromMilliseconds(Options.StateMachine.HandshakeTimeoutMs);
        var deadline = DateTime.UtcNow + timeout;
        var started = Stopwatch.GetTimestamp();
        string? lastObservation = null;
        var lastLoggedAt = started;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = await CraneDriver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            var observation = dto is null ? "实时状态不可用" :
                $"显示XYZ=({dto.XDisplay},{dto.YDisplay},{dto.ZDisplay})，编码器XYZ=({dto.XEncoder},{dto.YEncoder},{dto.ZEncoder})，" +
                $"有板={FormatLogValue(dto.HasPlate)}，充磁={FormatLogValue(dto.MagnetizeFeedback)}，退磁={FormatLogValue(dto.DemagnetizeFeedback)}，报警={dto.HasAlarm}";
            if (observation != lastObservation || Stopwatch.GetElapsedTime(lastLoggedAt).TotalMilliseconds >= 2000)
            {
                _logger.Info(LogCategory.StateMachine, $"{LogTag} 等待天车 {description} 观测：{observation}，已等待={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
                lastObservation = observation;
                lastLoggedAt = Stopwatch.GetTimestamp();
            }
            if (dto is not null && dto.HasAlarm)
            {
                _logger.Warn(LogCategory.StateMachine, $"{LogTag} 等待天车 {description} 因报警中止：{observation}");
                throw new InvalidOperationException($"{Tag} 天车报警，等待 {description} 中止");
            }

            if (dto is not null && predicate(dto))
            {
                _logger.Info(LogCategory.StateMachine, $"{LogTag} 等待天车 {description} 满足：{observation}，耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.Warn(LogCategory.StateMachine, $"{LogTag} 等待天车 {description} 超时：{observation}，已等待={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms，上限={timeout.TotalMilliseconds}ms");
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
    /// <returns>异步等待任务；只观察已有快照，保留原信号判定。</returns>
    public async Task WaitDeviceAsync(DeviceBase device, string signal, bool expected, CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{LogTag} 等待 {device.Name}.{signal}={expected}：设备Id={device.Id}，期望={(expected ? 1 : 0)}，来源=后台采集缓存，超时={Options.StateMachine.HandshakeTimeoutMs}ms，轮询等待=200ms");
        var timeout = TimeSpan.FromMilliseconds(Options.StateMachine.HandshakeTimeoutMs);
        var deadline = DateTime.UtcNow + timeout;
        var started = Stopwatch.GetTimestamp();
        string? lastObservation = null;
        var lastLoggedAt = started;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 读采集器写入的**缓存快照**（不实时读 PLC，避免与 DeviceStateCollector 争连接）。
            // 信号必须真实读到：缺失视为"未知"，不算满足。
            // 否则 expected=false（如"下料架无板"）会把"没采到数据"误当成"无料"，可能往实际有板的架子放料。
            var snapshot = device.Signals;
            // 诊断只观察本轮已取得的快照；不因日志增加PLC读取，也不将序号变化当作信号变化刷盘。
            var observation = snapshot is null ? "未知（尚无快照）" :
                $"原值={(snapshot.Values.TryGetValue(signal, out var raw) ? FormatLogValue(raw) : "未知（缺少点位）")}，连接={snapshot.ConnectionState}，质量={snapshot.Quality}";
            var snapshotDetail = snapshot is null ? "无采集时间/序号" :
                $"采集时间={snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss.fff}，序号={snapshot.Sequence}，快照年龄={(DateTime.Now - snapshot.CapturedAt).TotalMilliseconds:0}ms";
            if (observation != lastObservation || Stopwatch.GetElapsedTime(lastLoggedAt).TotalMilliseconds >= 2000)
            {
                _logger.Info(LogCategory.StateMachine,
                    $"{LogTag} 等待 {device.Name}.{signal} 观测：期望={(expected ? 1 : 0)}，{observation}，{snapshotDetail}，已等待={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
                lastObservation = observation;
                lastLoggedAt = Stopwatch.GetTimestamp();
            }
            if (snapshot is not null && snapshot.Values.TryGetValue(signal, out var v))
            {
                var value = v is bool b && b;
                if (value == expected)
                {
                    _logger.Info(LogCategory.StateMachine,
                        $"{LogTag} 等待 {device.Name}.{signal} 满足：期望={(expected ? 1 : 0)}，{observation}，{snapshotDetail}，耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
                    return;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.Warn(LogCategory.StateMachine,
                    $"{LogTag} 等待 {device.Name}.{signal} 超时：期望={(expected ? 1 : 0)}，{observation}，{snapshotDetail}，已等待={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms，上限={timeout.TotalMilliseconds}ms");
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
    /// <returns>异步写入任务；驱动成功返回后才记录完成，原异常继续向上传播。</returns>
    public async Task WriteDeviceAsync(DeviceBase device, string signal, object? value, CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.StateMachine, $"{LogTag} 写入开始：设备[{device.Name}/Id={device.Id}/类型={device.Type}]，点名={signal}，下发值={FormatLogValue(value)}；等待驱动写入返回");
        var started = Stopwatch.GetTimestamp();
        await DeviceDriver(device.Id).WriteSignalAsync(signal, value, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.StateMachine, $"{LogTag} 写入完成：设备[{device.Name}/Id={device.Id}]，点名={signal}，下发值={FormatLogValue(value)}，耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms；写入返回成功，不代表设备工艺动作已完成");
    }

    // 仅呈现原命令结果，不参与成败、取消或充磁边界的业务判断。
    private void LogCommandResult(string action, CraneCommandResult result, long started)
    {
        var message = $"{LogTag} {action}返回：成功={result.IsSuccess}，结果码={result.Code}，详情={result.Message}，耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms";
        if (result.IsSuccess) _logger.Info(LogCategory.StateMachine, message);
        else _logger.Warn(LogCategory.StateMachine, message);
    }

    // 保留未知及原类型，打印内容转义换行，防止一次写入的数据拆散为多条伪日志。
    private static string FormatLogValue(object? value) => value switch
    {
        null => "未知(null)",
        bool flag => flag ? "1(Boolean)" : "0(Boolean)",
        string text => $"「{text.Replace("\r", "\\r").Replace("\n", "\\n")}」(String)",
        _ => $"{value}({value.GetType().Name})"
    };

    /// <summary>
    /// 写一条**流程步骤**日志：自动带 **天车 + 工件**，并标注**设备**与当前走到哪一步。
    /// 与各原语（运动/等待/读写）的日志配合，可在 `statemachine.txt` 里按"天车 / 工件 / 设备 / 步骤"
    /// 串起一次完整搬运，便于现场排查卡在哪一步。
    /// </summary>
    /// <param name="device">当前操作的设备</param>
    /// <param name="action">步骤说明（如"取料：Z 下降到取料位"）</param>
    public void LogStep(DeviceBase device, string action)
    {
        PublishStep(action);
        _logger.Info(LogCategory.StateMachine, $"{LogTag} [{device.Name}] {action}");
    }

    /// <summary>写一条**流程步骤**日志（不针对具体设备，如收尾阶段）。</summary>
    /// <param name="action">步骤说明</param>
    public void LogStep(string action)
    {
        //更新天车字段CurrentStep  大屏展示页面会进行显示
        PublishStep(action);
        _logger.Info(LogCategory.StateMachine, $"{LogTag} {action}");
    }

    // 不解析日志文本；沿用业务明确记录步骤的时机发布只读说明，任务释放后不复活旧步骤。
    private void PublishStep(string action)
    {
        if (Crane.Runtime.CurrentTask is not null) Crane.Runtime.CurrentStep = action;
    }

    /// <summary>
    /// 写一条**流程步骤警告**日志（同样自动带 天车 + 工件）——
    /// 用于"本步失败但仍会继续或回滚"的场景，保留 WARN 级别便于现场按级别筛选。
    /// </summary>
    /// <param name="action">步骤说明</param>
    public void LogStepWarn(string action)
        => _logger.Warn(LogCategory.StateMachine, $"{LogTag} {action}");
}
