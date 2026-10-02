using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 下料架：只作目标（放料）。无参数、无请求上料、无"上料完成"，放料完成脉冲现场暂不需要。
/// </summary>
public sealed class UnloadRackHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.UnloadRack;

    /// <inheritdoc />
    public Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
        => throw new NotSupportedException("下料架不作为取料源");

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        // 审核目标：无板
        await ctx.WaitDeviceAsync(target, "HasMaterial", false, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, ctx.Workpiece.Info.Diameter);
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
    }
}
