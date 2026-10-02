using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 设备取放料策略：每类设备一个实现，内部是一整串线性流程。
/// 编排器只按 <see cref="DeviceType"/> 分派 Pick/Place。
/// </summary>
public interface ITransferHandler
{
    /// <summary>是否处理该设备类型。</summary>
    /// <param name="type">设备类型</param>
    /// <returns>处理返回 true</returns>
    bool CanHandle(DeviceType type);

    /// <summary>从源设备取料（含源审核、XY/Z、充磁、源特有交互）。</summary>
    /// <param name="ctx">流程上下文</param>
    /// <param name="source">源设备</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task PickAsync(TransferContext ctx, DeviceBase source, CancellationToken cancellationToken);

    /// <summary>向目标设备放料（含目标审核、参数、Z、目标特有交互、放磁）。</summary>
    /// <param name="ctx">流程上下文</param>
    /// <param name="target">目标设备</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task PlaceAsync(TransferContext ctx, DeviceBase target, CancellationToken cancellationToken);
}
