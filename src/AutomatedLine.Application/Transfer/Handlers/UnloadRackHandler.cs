using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer.Handlers;

/// <summary>
/// 下料架只作放料目标；无加工参数或请求上料握手，Z回安全高度后发1秒放料完成脉冲。
/// </summary>
public sealed class UnloadRackHandler : ITransferHandler
{
    /// <inheritdoc />
    public bool CanHandle(DeviceType type) => type == DeviceType.UnloadRack;

    /// <inheritdoc />
    /// <remarks>PlaceDone在Place内完成脉冲及异常复位，通用握手清理不重复下发。</remarks>
    public Task<int> ClearHandshakeAsync(IDeviceDriver driver, string deviceName, IAppLogger logger, CancellationToken cancellationToken)
        => Task.FromResult(0);

    /// <inheritdoc />下料架没有下料操作
    public Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken)
        => throw new NotSupportedException("下料架不作为取料源");

    /// <inheritdoc />
    /// <remarks>Z回SafeZ成功后才通知架体；PlaceDone写1返回后保持1000ms再写0，不等待整车回SafeX。</remarks>
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
        //下料架写1-1000ms 写0
        await PulsePlaceDoneAsync(ctx, target, cancellationToken).ConfigureAwait(false);
        ctx.LogStep(target, "放料：完成（下料架PlaceDone已写1、保持1000ms并写0）");
    }

    // 写1的应答丢失也可能意味着PLC已收到；开始尝试写1后必须独立限时复位，且不能覆盖原失败。
    private static async Task PulsePlaceDoneAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? pulseFailure = null;
        try
        {
            ctx.LogStep(target, "放料：下料完成脉冲写1（PlaceDone，FC05/PDU1），等待写入应答");
            await ctx.WriteDeviceAsync(target, "PlaceDone", 1, cancellationToken).ConfigureAwait(false);
            ctx.LogStep(target, "放料：PlaceDone写1已返回，开始保持1000ms");
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            pulseFailure = ex;
            throw;
        }
        finally
        {
            // 复位不能沿用已取消的搬运令牌，也不能无限等待通信锁；复用既有设备通信期限。
            var configuredTimeout = ctx.Options.Collection.CommunicationTimeoutMs;
            var resetTimeoutMs = configuredTimeout > 0 ? configuredTimeout : 3000;
            using var reset = new CancellationTokenSource(resetTimeoutMs);
            try
            {
                ctx.LogStep(target, $"放料：下料完成脉冲复位写0（PlaceDone，FC05/PDU1），独立超时={resetTimeoutMs}ms，等待写入应答");
                await ctx.WriteDeviceAsync(target, "PlaceDone", 0, reset.Token).ConfigureAwait(false);
                ctx.LogStep(target, "放料：PlaceDone复位写0已返回");
            }
            catch (Exception resetError)
            {
                ctx.LogStepWarn($"下料架[{target.Name}/Id={target.Id}] PlaceDone复位失败（FC05/PDU1=0）：{resetError.Message}；" +
                    (pulseFailure is null ? "本次放料不能判为完成" : $"保留原失败：{pulseFailure.Message}"));
                if (pulseFailure is null) throw;
            }
        }
    }
}
