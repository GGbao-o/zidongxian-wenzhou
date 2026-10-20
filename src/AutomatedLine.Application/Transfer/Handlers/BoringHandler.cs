using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 带托料架双头镗：取料充磁后回升，放料退磁后回升，不使用卡钳中间握手。
/// Z回安全高度成功后才发送取放完成；下发板长、外径、堵孔、堵厚四个加工参数。
/// </summary>
public sealed class BoringHandler : ITransferHandler
{
    /// <summary>
    /// 本机"上位机写入的握手位"（电平信号，下一次上料前必须复位为 0）：
    /// DataSentDone(12205)、LoadDone(12213)、UnloadDone(12221)。
    /// </summary>
    private static readonly string[] WrittenHandshakePoints =
        { "DataSentDone", "LoadDone", "UnloadDone" };

    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.BoringMachine;

    /// <inheritdoc />
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => HandshakeReset.ClearAsync(driver, deviceName, WrittenHandshakePoints, logger, cancellationToken);

    /// <inheritdoc /> 下料操作
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        ctx.LogStep(source, "取料：开始（等设备请求下料 RequestUnload，12215=1）");
        // 轮询设备输入信号直到等于期望值
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);

        //拿到的xyz三个值是加了对应天车偏移的
        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        //三轴运动   去source上方
        ctx.LogStep(source, $"取料：XY → 设备上方（X={x} Y={y} Z={ctx.Crane.SafeZ}）");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        //进行微调操作
        await ctx.RefineXyAsync(x, y, cancellationToken).ConfigureAwait(false);
        //计算下降距离(绝对值编码器的值)
        var zTarget = z - CraneCoordinate.ZDrop(source.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(source, $"取料：Z 下降到取料位（Z={zTarget}，工件直径 {ctx.Workpiece.Info.Diameter}）");
        //绝对移动
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);
        // Z 到位后等待有板信号稳定 再读取x11有板信号
        await ctx.CheckPlateBeforeMagnetAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：X11 有板 → 充磁");
        // 充磁
        // 从取料充磁开始就可能与设备交接，调用失败也沿用弃板，不能等反馈成功再标记。
        ctx.MagnetCommandStarted = true;
        //充磁  ----------------------------------------------------------------------------------------------------------------------------------------------------------从这之后的异常 板就需要丢弃
        await ctx.MagnetOnAsync(cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：充磁完成（板已吸住，此后异常不可回滚）");
        // 托料架协议没有松开反馈；板吸住且Z回升成功后才允许设备收到下料完成。
        //safez没有+偏移
        ctx.LogStep(source, $"取料：充磁成功，Z 回升到安全高度（SafeZ={ctx.Crane.SafeZ}）");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        //发下料完成
        ctx.LogStep(source, "取料：写 天车下料退出完成 UnloadDone，12221=1");
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：完成（可离开源设备）");
    }

    /// <inheritdoc />上料操作
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, "放料：开始（等设备请求数据 RequestData，12203=1）");
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：下发四个加工参数（板长301、外径303、堵孔305=工件70/100、堵厚309）");
        await WriteParamsAsync(ctx, target, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：四个参数全部写入成功，写 数据下发完成 DataSentDone，12205=1");
        await ctx.WriteDeviceAsync(target, "DataSentDone", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：等设备请求上料 RequestLoad，12207=1");
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(target, $"放料：Z 下降到放料位（X={x} Y={y} Z={zTarget}）");
        //天车下将z轴目标位置
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        // 工件放在托料架上，不使用夹紧反馈；完成位必须等退磁和回升均成功后再发。
        ctx.LogStep(target, "放料：Z 下降到位，开始退磁（带托料架，无卡钳中间握手）");
        //放磁
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);

        //回安全位置 
        ctx.LogStep(target, $"放料：退磁成功，Z 回升到安全高度（SafeZ={ctx.Crane.SafeZ}）");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        // 图纸称上料到位，但注明磁铁上去再发；逻辑仍为完整放料结束后的上料完成。
        ctx.LogStep(target, "放料：写 天车上料退出完成 LoadDone，12213=1");
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：完成");
    }

    /// <summary>依次下发四个原始参数；仅驱动编码Word，不修改工件数据供其他工序使用。</summary>
    private static async Task WriteParamsAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        var info = ctx.Workpiece.Info;
        ctx.LogStep(target, $"放料参数：Length 301，录入={info.Length}，Word={(ushort)Convert.ToInt32(info.Length)}；" +
                            $"Diameter 303，录入={info.Diameter}，倍率10，Word={(ushort)Convert.ToInt32(info.Diameter * 10)}；" +
                            $"BorePlug 305，工件PlugHole={info.PlugHole}，倍率1，Word={(ushort)info.PlugHole}；" +
                            $"PlugThickness 309，录入={info.PlugThickness}，Word={(ushort)Convert.ToInt32(info.PlugThickness)}");
        await ctx.WriteDeviceAsync(target, "Length", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "Diameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        // 305明确使用工件的70/100堵孔值；等待写入应答后再继续，避免设备收到缺少第四项的数据完成信号。
        await ctx.WriteDeviceAsync(target, "BorePlug", info.PlugHole, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "PlugThickness", info.PlugThickness, cancellationToken).ConfigureAwait(false);
    }
}
