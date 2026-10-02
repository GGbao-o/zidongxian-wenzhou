using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 打号机：取料（加工完成→有板→充磁→Z升→下料完成）；放料（空闲→下发打印内容→Z降→放磁→Z升→上料完成）。
/// </summary>
public sealed class MarkerHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.MarkerMachine;

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(source, "ProcessDone", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);

        // 源特有：Z 升 → 下料完成
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(target, "Idle", true, cancellationToken).ConfigureAwait(false);

        // 下发打印内容（FC16）、外径（=板长）、内径（=堵孔 70/100）
        var info = ctx.Workpiece.Info;
        await ctx.WriteDeviceAsync(target, "PrintContent", info.PrintContent, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerOuterDiameter", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerInnerDiameter", info.PlugHole, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, info.Diameter);
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
    }
}
