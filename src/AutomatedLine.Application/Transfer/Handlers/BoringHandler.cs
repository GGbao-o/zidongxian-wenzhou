using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 双头镗：取料充磁后通知到位、等卡钳松开再回升；放料到位后等卡钳夹紧再退磁回升。
/// 完整取放结束才发送退出完成；只下发板长、外径、堵厚三个加工参数。
/// </summary>
public sealed class BoringHandler : ITransferHandler
{
    /// <summary>
    /// 本机"上位机写入的握手位"（电平信号，下一次上料前必须复位为 0）：
    /// DataSentDone(12205)、LoadInPlace(12209)、LoadDone(12213)、UnloadInPlace(12217)、UnloadDone(12221)。
    /// 夹紧12211/松开12219是设备输入，不能作为上位机输出清零。
    /// </summary>
    private static readonly string[] WrittenHandshakePoints =
        { "DataSentDone", "LoadInPlace", "LoadDone", "UnloadInPlace", "UnloadDone" };

    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.BoringMachine;

    /// <inheritdoc />
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => HandshakeReset.ClearAsync(driver, deviceName, WrittenHandshakePoints, logger, cancellationToken);

    /// <inheritdoc /> 上料架->双头镗 取料操作
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        ctx.LogStep(source, "取料：开始（等设备请求下料 RequestUnload，12215=1）");
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
        // 板先被吸住再让设备松开；反馈等待失败仍属于取料充磁后的弃板边界。
        ctx.LogStep(source, "取料：写 天车下料到位 UnloadInPlace，12217=1");
        await ctx.WriteDeviceAsync(source, "UnloadInPlace", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：等卡钳松开完成 UnloadReleaseDone，12219=1");
        await ctx.WaitDeviceAsync(source, "UnloadReleaseDone", true, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：卡钳松开反馈通过（12219=1），允许Z回升");
        // 源特有：确认松开 → Z 升 → 下料退出完成
        //safez没有+偏移
        ctx.LogStep(source, "取料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        //发下料完成
        ctx.LogStep(source, "取料：写 天车下料退出完成 UnloadDone，12221=1");
        await ctx.WriteDeviceAsync(source, "UnloadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(source, "取料：完成（可离开源设备）");
    }

    /// <inheritdoc />放料操作 上料架->双头镗
    public async Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        ctx.LogStep(target, "放料：开始（等设备请求数据 RequestData，12203=1）");
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：下发三个加工参数（板长301、外径303、堵厚309）");
        await WriteParamsAsync(ctx, target, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：三个参数全部写入成功，写 数据下发完成 DataSentDone，12205=1");
        await ctx.WriteDeviceAsync(target, "DataSentDone", 1, cancellationToken).ConfigureAwait(false);

        ctx.LogStep(target, "放料：等设备请求上料 RequestLoad，12207=1");
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, ctx.Workpiece.Info.Diameter);
        ctx.LogStep(target, $"放料：Z 下降到放料位（X={x} Y={y} Z={zTarget}）");
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        // 工件尚由天车承托，必须收到夹紧反馈后才退磁；到位通知与退出完成使用不同地址。
        ctx.LogStep(target, "放料：写 天车上料到位 LoadInPlace，12209=1");
        await ctx.WriteDeviceAsync(target, "LoadInPlace", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：等卡钳夹紧完成 LoadLockDone，12211=1");
        await ctx.WaitDeviceAsync(target, "LoadLockDone", true, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：卡钳夹紧反馈通过（12211=1），开始退磁");
        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);

        //回安全位置 
        ctx.LogStep(target, "放料：Z 回升到安全高度");
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);

        //发下料完成
        ctx.LogStep(target, "放料：写 天车上料退出完成 LoadDone，12213=1");
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：完成");
    }

    /// <summary>依次下发三个原始参数；仅驱动编码Word，不修改工件数据供其他工序使用。</summary>
    private static async Task WriteParamsAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        var info = ctx.Workpiece.Info;
        ctx.LogStep(target, $"放料参数：Length 301，录入={info.Length}，Word={(ushort)Convert.ToInt32(info.Length)}；" +
                            $"Diameter 303，录入={info.Diameter}，倍率10，Word={(ushort)Convert.ToInt32(info.Diameter * 10)}；" +
                            $"PlugThickness 309，录入={info.PlugThickness}，Word={(ushort)Convert.ToInt32(info.PlugThickness)}");
        await ctx.WriteDeviceAsync(target, "Length", info.Length, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "Diameter", info.Diameter, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "PlugThickness", info.PlugThickness, cancellationToken).ConfigureAwait(false);
    }
}
