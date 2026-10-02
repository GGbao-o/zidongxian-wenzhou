using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 沈阳斜床：取/放料均含 Y 二次偏移（每台一份参数，见《CraneStateMachine设计.md》§9.1）。
/// </summary>
public sealed class LatheHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.LatheMachine;

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        var yOffset = OffsetOf(ctx, source.Id);
        var pickY = y - yOffset;   // 二次偏移位置

        await ctx.MoveAbsoluteAsync(x, pickY, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        await ctx.MoveAbsoluteAsync(x, pickY, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);

        // 源特有：下料到达 → 尾座张开 → Y 回库位 → Z 升 → 下料完成
        await ctx.WriteDeviceAsync(source, "CraneUnloadInPlace", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(source, "TailstockOpened", true, cancellationToken).ConfigureAwait(false);
        await ctx.MoveRelativeAsync(0, yOffset, 0, cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(source, "CraneUnloadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        var info = ctx.Workpiece.Info;
        await ctx.WriteDeviceAsync(target, "RollerLength", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerDiameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "BorePlug", info.PlugHole, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "MachiningMode", 0, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "DataSentDone", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var yOffset = OffsetOf(ctx, target.Id);

        var zTarget = z - CraneCoordinate.ZDrop(target.Type, info.Diameter);
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        // 目标特有：Y 二次偏移 → 尾座夹紧
        await ctx.MoveRelativeAsync(0, -yOffset, 0, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "TailstockClamped", true, cancellationToken).ConfigureAwait(false);
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "CraneLoadDone", 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取该斜床的 Y 偏移量（按设备号，缺省用 0）。</summary>
    private static int OffsetOf(TransferContext ctx, int latheId)
    {
        var info = ctx.Workpiece.Info;
        if (!ctx.Options.Transfer.Lathes.TryGetValue(latheId, out var opt))
        {
            return 0;
        }

        return LatheYOffset.Compute(opt, info.PlugHole, info.Length);
    }
}
