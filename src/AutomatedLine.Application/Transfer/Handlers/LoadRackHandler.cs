using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 料架（上料架）：只作源（取料），无握手输出。
/// </summary>
public sealed class LoadRackHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.LoadRack;

    /// <inheritdoc />
    /// <remarks>上料架只作源、没有"上位机写入位"，无需复位。</remarks>
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => Task.FromResult(0);

    /// <inheritdoc />
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        ctx.LogStep(source, "取料：开始（等上料架有板 HasMaterial=1）");
        // 审核源：上料架有板
        await ctx.WaitDeviceAsync(source, "HasMaterial", true, cancellationToken).ConfigureAwait(false);

        // 到上方 → Z 下降到取料位
        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, $"取料：XY → 料架上方（X={x} Y={y} Z={ctx.Crane.SafeZ}）");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(source, $"取料：Z 下降到取料位（Z={zTarget}，工件直径 {ctx.Workpiece.Info.Diameter}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        // 有板 → 充磁（边界）→ Z 回安全
        await ctx.WaitCraneAsync(dto => dto.HasPlate == true, "X11 有板", cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：X11 有板 → 充磁");
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：充磁完成（板已吸住，此后异常不可回滚）");

        ctx.LogStep(source, "取料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：完成（可离开源设备）");
    }

    /// <inheritdoc />
    public Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
        => throw new NotSupportedException("上料架不作为放料目标");
}
