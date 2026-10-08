using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 新代研磨机：取料（请求下料→有板→充磁→下料到达松开位→松开完成退出→Z升→下料完成）；
/// 放料（请求数据→下发参数+传输完成→请求上料→Z降→到达锁紧位→锁紧完成退出→放磁→Z升→上料完成）。
/// </summary>
public sealed class GrinderHandler : ITransferHandler
{
    /// <summary>
    /// 本机"上位机写入的握手位"（电平信号，下一次上料前必须复位为 0）：
    /// `DataTransDone`(R7311/14623)、`LoadInPlace`(R7312/14625)、`LoadDone`(R7313/14627)、
    /// `UnloadInPlace`(R7314/14629)、`UnloadDone`(R7315/14631)。
    /// </summary>
    private static readonly string[] WrittenHandshakePoints =
        { "DataTransDone", "LoadInPlace", "LoadDone", "UnloadInPlace", "UnloadDone" };

    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.GrinderMachine;

    /// <inheritdoc />
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => HandshakeReset.ClearAsync(driver, deviceName, WrittenHandshakePoints, logger, cancellationToken);

    /// <inheritdoc /> 研磨机取料
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        ctx.LogStep(source, "取料：开始（等设备请求下料 RequestUnload=1）");
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, $"取料：XY → 设备上方（X={x} Y={y} Z={ctx.Crane.SafeZ}）");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(source, $"取料：Z 下降到取料位（Z={zTarget}，工件直径 {ctx.Workpiece.Info.Diameter}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：X11 有板 → 充磁");
        // 取料充磁调用失败也按充磁后弃板，标记不依赖反馈是否成功。
        ctx.MagnetCommandStarted = true;
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：充磁完成（板已吸住，此后异常不可回滚）");

        // 源特有：下料到达松开位置 → 松开完成退出 → Z 升 → 下料完成
        ctx.LogStep(source, "取料：写 下料到达松开位置=1（UnloadInPlace）");
        await ctx.WriteDeviceAsync(source, "UnloadInPlace", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(source, "取料：等设备松开完成并退出（UnloadReleaseDone=1）");
        await ctx.WaitDeviceAsync(source, "UnloadReleaseDone", true, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(source, "取料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(source, "取料：写 下料完成=1（UnloadDone）");
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：完成（可离开源设备）");
    }

    /// <inheritdoc /> 研磨机放料
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, "放料：开始（等设备请求数据 RequestData=1）");
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        var info = ctx.Workpiece.Info;
        ctx.LogStep(target, $"放料：下发加工参数（直径 {info.Diameter}、长度 {info.Length}、加工模式 {info.PlugHole}）");
        await ctx.WriteDeviceAsync(target, "RollerDiameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerLength", info.Length, cancellationToken).ConfigureAwait(false);
        // 加工模式：1=大孔(PlugHole=100)、2=小孔(PlugHole=70)
        await ctx.WriteDeviceAsync(target, "MachiningMode", info.PlugHole == 100 ? 1 : 2, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：写 数据传输完成=1（DataTransDone）");
        await ctx.WriteDeviceAsync(target, "DataTransDone", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：等设备请求上料 RequestLoad=1");
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, info.Diameter);
        ctx.LogStep(target, $"放料：Z 下降到放料位（X={x} Y={y} Z={zTarget}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        // 目标特有：到达锁紧位置 → 锁紧完成退出 → 放磁 → Z 升 → 上料完成
        ctx.LogStep(target, "放料：写 上料到达锁紧位置=1（LoadInPlace）");
        await ctx.WriteDeviceAsync(target, "LoadInPlace", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：等设备锁紧完成并退出（LoadLockDone=1）");
        await ctx.WaitDeviceAsync(target, "LoadLockDone", true, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：放磁");
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：写 上料完成=1（LoadDone）");
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：完成");
    }
}
