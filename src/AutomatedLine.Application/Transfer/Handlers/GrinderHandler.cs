using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 新代研磨机：取料（请求下料→有板→充磁→下料到达松开位→松开完成退出→Z升→下料完成）；
/// 放料（请求数据→下发参数+传输完成→请求上料→Z降→到达锁紧位→锁紧完成退出→放磁→Z升→上料完成）。
/// </summary>
public sealed class GrinderHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.GrinderMachine;

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);

        // 源特有：下料到达松开位置 → 松开完成退出 → Z 升 → 下料完成
        await ctx.WriteDeviceAsync(source, "UnloadInPlace", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(source, "UnloadReleaseDone", true, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        var info = ctx.Workpiece.Info;
        await ctx.WriteDeviceAsync(target, "RollerDiameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerLength", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "MachiningMode", info.PlugHole, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "DataTransDone", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, info.Diameter);
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        // 目标特有：到达锁紧位置 → 锁紧完成退出 → 放磁 → Z 升 → 上料完成
        await ctx.WriteDeviceAsync(target, "LoadInPlace", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "LoadLockDone", true, cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
    }
}
