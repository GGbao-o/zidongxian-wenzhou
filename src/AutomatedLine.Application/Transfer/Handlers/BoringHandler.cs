using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 双头镗：取料（请求下料→有板→充磁→Z升→下料完成）；
/// 放料（请求数据→下发参数+数据下发完成→请求上料→Z降→放磁→Z升→上料完成）。
/// </summary>
public sealed class BoringHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.BoringMachine;

    /// <inheritdoc /> 上料架->双头镗
    public async Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
    {
        // 轮询设备输入信号直到等于期望值
        await ctx.WaitDeviceAsync(source, "RequestUnload", true, cancellationToken).ConfigureAwait(false);
        //拿到的xyz三个值是加了偏移的
        var (x, y, z) = await ctx.BaseOfAsync(source, cancellationToken).ConfigureAwait(false);
        //三轴运动   去source上方
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
        await ctx.WaitDeviceAsync(target, "RequestData", true, cancellationToken).ConfigureAwait(false);
        await WriteParamsAsync(ctx, target, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "DataSentDone", 1, cancellationToken).ConfigureAwait(false);
        await ctx.WaitDeviceAsync(target, "RequestLoad", true, cancellationToken).ConfigureAwait(false);

        var (x, y, z) = await ctx.BaseOfAsync(target, cancellationToken).ConfigureAwait(false);
        var zTarget = z - CraneCoordinate.ZDrop(target.Type, ctx.Workpiece.Info.Diameter);
        await ctx.MoveAbsoluteAsync(x, y, zTarget, cancellationToken).ConfigureAwait(false);

        await ctx.MagnetOffAsync(cancellationToken).ConfigureAwait(false);
        await ctx.MoveAbsoluteAsync(x, y, ctx.Crane.SafeZ, cancellationToken).ConfigureAwait(false);
        await ctx.WriteDeviceAsync(target, "LoadDone", 1, cancellationToken).ConfigureAwait(false);
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
