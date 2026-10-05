using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 设备取放料策略：每类设备一个实现，内部是一整串线性流程。
/// 编排器只按 <see cref="DeviceType"/> 分派 Pick/Place，以及取料后的握手位复位。
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

    /// <summary>
    /// 尽力复位本机"上位机写入的握手位"（写 0）。只清握手位、不清加工参数；料架/打号机/下料架无此需求，实现为空操作。
    /// <para>
    /// **约定时机：本机作为源完成取料之后**（由编排器在中转到目标上方后调用）——
    /// 此时距写下"下料完成=1"已隔整段 XY 移动，PLC 必然已采到那个 1，无需额外等待或延时。
    /// **目标设备绝不能调用**：它马上就要上料，握手位正要被写 1。
    /// 手动操作页的「清握手位」也复用本方法。内部已逐点容错，调用方无需 try。
    /// </para>
    /// </summary>
    /// <param name="driver">本机驱动</param>
    /// <param name="deviceName">设备名（日志用）</param>
    /// <param name="logger">日志</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>成功复位的点数</returns>
    Task<int> ClearHandshakeAsync(
        IDeviceDriver driver,
        string deviceName,
        IAppLogger logger,
        CancellationToken cancellationToken);
}
