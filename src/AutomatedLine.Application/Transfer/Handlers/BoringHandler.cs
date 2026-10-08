using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 双头镗：取料（请求下料→有板→充磁→Z升→下料完成）；
/// 放料（请求数据→下发参数+数据下发完成→请求上料→Z降→放磁→Z升→上料完成）。
/// </summary>
public sealed class BoringHandler : ITransferHandler
{
    /// <summary>
    /// 本机"上位机写入的握手位"（电平信号，下一次上料前必须复位为 0）：
    /// `DataSentDone`(R6102/12205)、`LoadDone`(R6104/12209)、`UnloadDone`(R6108/12217)。
    /// </summary>
    private static readonly string[] WrittenHandshakePoints = { "DataSentDone", "LoadDone", "UnloadDone" };

    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.BoringMachine;

    /// <inheritdoc />
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => HandshakeReset.ClearAsync(driver, deviceName, WrittenHandshakePoints, logger, cancellationToken);

    /// <inheritdoc /> 上料架->双头镗 取料操作
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        ctx.LogStep(source, "取料：开始（等设备请求下料 RequestUnload=1）");
        // 轮询设备输入信号直到等于期望值
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);

        //拿到的xyz三个值是加了偏移的
        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        //三轴运动   去source上方
        ctx.LogStep(source, $"取料：XY → 设备上方（X={x} Y={y} Z={ctx.Crane.SafeZ}）");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(source, $"取料：Z 下降到取料位（Z={zTarget}，工件直径 {ctx.Workpiece.Info.Diameter}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);
        // Z 到位后等待信号稳定，再单次确认有板；失败仍属于充磁前。
        await ctx.CheckPlateBeforeMagnetAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：X11 有板 → 充磁");
        //冲磁
        // 从取料充磁开始就可能与设备交接，调用失败也沿用弃板，不能等反馈成功再标记。
        ctx.MagnetCommandStarted = true;
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：充磁完成（板已吸住，此后异常不可回滚）");

        // 源特有：Z 升 → 下料完成
        //safez没有+偏移
        ctx.LogStep(source, "取料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        //发下料完成
        ctx.LogStep(source, "取料：写 下料完成=1（UnloadDone）");
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：完成（可离开源设备）");
    }

    /// <inheritdoc />放料操作 上料架->双头镗
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, "放料：开始（等设备请求数据 RequestData=1）");
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：下发加工参数（长度/直径/左右堵厚/内孔锥度/堵孔/圆角）");
        await WriteParamsAsync(ctx, target, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：写 数据下发完成=1（DataSentDone）");
        await ctx.WriteDeviceAsync(target, "DataSentDone", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：等设备请求上料 RequestLoad=1");
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(target, $"放料：Z 下降到放料位（X={x} Y={y} Z={zTarget}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：放磁");
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);

        //回安全位置 
        ctx.LogStep(target, "放料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        //发下料完成
        ctx.LogStep(target, "放料：写 上料完成=1（LoadDone）");
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：完成");
    }

    /// <summary>双头镗加工参数（长度原值；其余驱动内 ×100）。</summary>
    private static async Task WriteParamsAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        var info = ctx.Workpiece.Info;
        await ctx.WriteDeviceAsync(target, "Length", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "Diameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "LeftPlugThickness", info.LeftPlugThickness, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "RightPlugThickness", info.RightPlugThickness, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "InnerHoleTaper", info.InnerHoleTaper, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "BorePlug", info.PlugHole, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "FilletSize", info.FilletSize, cancellationToken).ConfigureAwait(false);
    }
}
