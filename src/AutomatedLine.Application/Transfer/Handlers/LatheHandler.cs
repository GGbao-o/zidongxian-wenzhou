using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 沈阳斜床：取/放料均含 Y 二次偏移（每台一份参数，见《应用层设计.md》§7.2）。
/// </summary>
public sealed class LatheHandler : ITransferHandler
{
    /// <summary>
    /// 本机"上位机写入的握手位"（宏变量电平信号，下一次上料前必须复位为 0）：
    /// `DataSentDone`(#1101)、`CraneLoadInPlace`(#1102)、`CraneLoadDone`(#1103)、
    /// `CraneUnloadInPlace`(#1104)、`CraneUnloadDone`(#1105)。
    /// </summary>
    private static readonly string[] WrittenHandshakePoints =
        { "DataSentDone", "CraneLoadInPlace", "CraneLoadDone", "CraneUnloadInPlace", "CraneUnloadDone" };

    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.LatheMachine;

    /// <inheritdoc />
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => HandshakeReset.ClearAsync(driver, deviceName, WrittenHandshakePoints, logger, cancellationToken);

    /// <inheritdoc />  斜床取料
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        ctx.LogStep(source, "取料：开始（等设备请求下料 RequestUnload=1）");
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);
        //获取设备xyz坐标并且➕天车偏移
        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        var yOffset = OffsetOf(ctx, source.Id);
        var pickY = y - yOffset;   // 二次偏移位置

        ctx.LogStep(source, $"取料：XY → 设备上方并做 Y 二次偏移（X={x} Y={pickY}，Y偏移={yOffset}）");
        //y到偏移位置
        await ctx.MoveAbsoluteAsync(x, pickY, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(source, $"取料：Z 下降到取料位（Z={zTarget}，工件直径 {ctx.Workpiece.Info.Diameter}）");
        await ctx.MoveAbsoluteAsync(x, pickY, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.CheckPlateBeforeMagnetAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：X11 有板 → 充磁");
        // 取料充磁调用失败也按充磁后弃板，标记不依赖反馈是否成功。
        ctx.MagnetCommandStarted = true;
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：充磁完成（板已吸住，此后异常不可回滚）");

        // 源特有：下料到达 → 尾座张开 → Y 回库位 → Z 升 → 下料完成
        ctx.LogStep(source, "取料：写 天车下料到位=1（CraneUnloadInPlace）");
        await ctx.WriteDeviceAsync(source, "CraneUnloadInPlace", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(source, "取料：等机床尾座张开（TailstockOpened=1）");
        await ctx.WaitDeviceAsync(source, "TailstockOpened", true, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(source, $"取料：Y 回工件中心（相对 +{yOffset}）");
        await ctx.MoveRelativeAsync(0, yOffset, 0, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(source, "取料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(source, "取料：写 天车下料完成=1（CraneUnloadDone）");
        await ctx.WriteDeviceAsync(source, "CraneUnloadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：完成（可离开源设备）");
    }

    /// <inheritdoc /> 斜床放料
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, "放料：开始（等设备请求数据 RequestData=1）");
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        var info = ctx.Workpiece.Info;
        ctx.LogStep(target, $"放料：下发加工参数（长度 {info.Length}、直径 {info.Diameter}、堵孔 {info.PlugHole}、斜床工艺 {info.LatheMachiningMode}）");
        await ctx.WriteDeviceAsync(target, "RollerLength", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RollerDiameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "BorePlug", info.PlugHole, cancellationToken).ConfigureAwait(false);
        // #909 加工模式：按工件录入的"斜床工艺"原样下发（1~10），不再写死
        await ctx.WriteDeviceAsync(target, "MachiningMode", info.LatheMachiningMode, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：写 数据下发完成=1（DataSentDone）");
        await ctx.WriteDeviceAsync(target, "DataSentDone", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：等设备请求上料 RequestLoad=1");
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var yOffset = OffsetOf(ctx, target.Id);

        var zTarget = z - CraneCoordinate.ZDrop(target.Type, info.Diameter);
        ctx.LogStep(target, $"放料：Z 下降到放料位（X={x} Y={y} Z={zTarget}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        // 目标特有：Y 二次偏移 → 发天车上料到位(#1102) → 尾座夹紧
        ctx.LogStep(target, $"放料：Y 二次偏移进入顶尖（相对 −{yOffset}）");
        //相对移动 
        await ctx.MoveRelativeAsync(0, -yOffset, 0, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：写 天车上料到位=1（CraneLoadInPlace）");
        await ctx.WriteDeviceAsync(target, "CraneLoadInPlace", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：等机床尾座夹紧（TailstockClamped=1）");
        await ctx.WaitDeviceAsync(target, "TailstockClamped", true, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：放磁");
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：写 天车上料完成=1（CraneLoadDone）");
        await ctx.WriteDeviceAsync(target, "CraneLoadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：完成");
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
