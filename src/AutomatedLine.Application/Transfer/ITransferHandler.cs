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
    /// 尽力复位本机"上位机写入的握手位"（写 0）。只清握手位、不清加工参数；上料架/下料架无此需求，实现为空操作。
    /// <para>
    /// **约定时机（由状态机持有对应预约时调用）**：
    /// ① 本机作为**源**完成取料之后 —— 清掉刚写出的"下料完成"等位（此时距写下已隔整段 XY 移动，PLC 必然已采到那个 1，
    /// 无需额外等待或延时；且与"释放源预约"同一处，语义都是"与源设备的业务关系结束"）；
    /// ② 本机作为**目标**开始放料之前 —— 清掉**上一轮残留**，保证放料阶段每次写 1 都是干净的 **0→1 上升沿**
    /// （残留 1 会让机器收不到这次下发 → 白等到握手超时；冷启动、上一轮失败中止后尤其常见）。
    /// ③ 本机作为**目标**完成放料且天车回安全位成功后 —— 清掉本次握手，再释放目标预约，避免清理与新任务写入交错。
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
