using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 打号机：取料（加工完成→有板→充磁→Z升→下料完成）；放料（空闲→下发打印内容→Z降→放磁→Z升→上料完成）。
/// </summary>
public sealed class MarkerHandler : ITransferHandler
{
    /// <summary>只复位取放完成握手，避免上一轮残留1；保留打印内容、内外径及设备只读状态。</summary>
    private static readonly string[] WrittenHandshakePoints = { "LoadDone", "UnloadDone" };

    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.MarkerMachine;

    /// <inheritdoc />
    /// <remarks>源侧取料后中转清理、目标侧放料前及正常收尾回安全位后，将0005/0006（PDU4/5）写0；逐点容错和取消传播沿用统一实现。</remarks>
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => HandshakeReset.ClearAsync(driver, deviceName, WrittenHandshakePoints, logger, cancellationToken);

    /// <inheritdoc />
    ///
    
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        ctx.LogStep(source, "取料：开始（等设备打印完成 ProcessDone=1）");
        await ctx.WaitDeviceAsync(source, "ProcessDone", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, $"取料：XY → 设备上方（X={x} Y={y} Z={ctx.Crane.SafeZ}）");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.RefineXyAsync(x, y, cancellationToken).ConfigureAwait(false);

        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(source, $"取料：Z 下降到取料位（Z={zTarget}，工件直径 {ctx.Workpiece.Info.Diameter}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.CheckPlateBeforeMagnetAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：X11 有板 → 充磁");
        // 取料充磁调用失败也按充磁后弃板，标记不依赖反馈是否成功。
        ctx.MagnetCommandStarted = true;
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：充磁完成（板已吸住，此后异常不可回滚）");

        // 源特有：Z 升 → 下料完成
        ctx.LogStep(source, "取料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(source, "取料：写 下料完成=1（UnloadDone）");
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：完成（可离开源设备）");
    }

    /// <inheritdoc />
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, "放料：开始（等设备空闲 Busy=false）");
        // 等"设备空闲"：0001 的 0 表示空闲，故期望 Busy=false（不要写成 Idle=true）
        await ctx.WaitDeviceAsync(target, "Busy", false, cancellationToken).ConfigureAwait(false);

        // 下发打印内容（FC16）、外径（=工件直径）、内径（=堵孔 70/100）
        var info = ctx.Workpiece.Info;
        ctx.LogStep(target, $"放料：下发打印内容「{info.PrintContent}」+ 外径 {info.Diameter} + 内径 {info.PlugHole}");
        await ctx.WriteDeviceAsync(target, "PrintContent", info.PrintContent, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerOuterDiameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerInnerDiameter", info.PlugHole, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, info.Diameter);
        ctx.LogStep(target, $"放料：Z 下降到放料位（X={x} Y={y} Z={zTarget}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：放磁");
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：写 上料完成=1（LoadDone）");
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：完成");
    }
}
