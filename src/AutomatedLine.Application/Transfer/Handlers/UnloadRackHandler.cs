using AutomatedLine.Application.Abstractions;
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
    /// <remarks>下料架只作目标、没有"上位机写入位"，无需复位。</remarks>
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => Task.FromResult(0);

    /// <inheritdoc />
    public Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
        => throw new NotSupportedException("下料架不作为取料源");

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, "放料：开始（等下料架无板 HasMaterial=0）");
        // 审核目标：无板
        await ctx.WaitDeviceAsync(target, "HasMaterial", false, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(target, $"放料：Z 下降到放料位（X={x} Y={y} Z={zTarget}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：放磁");
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：完成（下料架无上料完成脉冲）");
    }
}
