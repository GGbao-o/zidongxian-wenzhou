using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// "上位机写入握手位"复位（**尽力清**，见《应用层设计.md》§5.5）。
/// <para>
/// 双头镗/斜床/研磨机/打号机的握手位是**电平**信号（上位机写 1 后一直保持），机器侧靠它们判断
/// "上位机已完成某一步"。若上一轮残留 1，下一轮可能收不到上升沿而误判，所以约定：
/// <b>设备作为源完成取料后</b>（清掉刚写出的位）、以及<b>作为目标开始放料前</b>（清掉上一轮残留，
/// 保证放料写 1 是干净的 0→1 上升沿），都把本机"上位机写入位"复位为 0。
/// 目标放料结束且天车回安全位成功后也复位一次；中转与收尾均由状态机在释放对应预约前等待清理返回。
/// 异常弃板时状态机也按交互及预约/工件归属复用此方法，使用独立期限；清理失败或取消只记录后继续原异常处置。
/// </para>
/// <para>
/// 只清握手位，<b>不清加工参数</b>（长度/直径/斜床工艺等下一轮上料必被重写，清 0 反而可能被机器读成中间态）。
/// 单个点写失败只记警告、不影响调用方流程——真清不掉最坏就是残留 1，退回清理前的行为，不比现状差。
/// </para>
/// </summary>
public static class HandshakeReset
{
    /// <summary>
    /// 逐个点名写 0。
    /// </summary>
    /// <param name="driver">设备驱动</param>
    /// <param name="deviceName">设备名（日志用）</param>
    /// <param name="points">要复位的点名；空表示该设备无需复位</param>
    /// <param name="logger">日志</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>成功复位的点数</returns>
    public static async Task<int> ClearAsync(
        IDeviceDriver driver,
        string deviceName,
        IReadOnlyList<string> points,
        IAppLogger logger,
        CancellationToken cancellationToken)
    {
        if (points.Count == 0)
        {
            // 上料架/下料架没有使用中的握手输出，空列表无需写PLC。
            return 0;
        }

        var cleared = 0;
        logger.Info(LogCategory.StateMachine,
            $"设备[{deviceName}/Id={driver.DeviceId}] 握手复位开始：依次写0，点名={string.Join('、', points)}；单点失败记警告后继续");
        foreach (var point in points)
        {
            try
            {
                logger.Info(LogCategory.StateMachine, $"设备[{deviceName}/Id={driver.DeviceId}] 握手位写入开始：{point}=0，等待驱动返回");
                await driver.WriteSignalAsync(point, 0, cancellationToken).ConfigureAwait(false);
                cleared++;
                logger.Info(LogCategory.StateMachine, $"设备[{deviceName}/Id={driver.DeviceId}] 握手位写入完成：{point}=0，已完成{cleared}/{points.Count}");
            }
            catch (OperationCanceledException)
            {
                // 取消要正常向上传播（与项目其它命令一致）
                throw;
            }
            catch (Exception ex)
            {
                // 尽力清：单点失败只记警告，不影响任务
                logger.Warn(LogCategory.StateMachine, $"{deviceName} 握手位 {point} 复位失败：{ex.Message}");
            }
        }

        logger.Info(LogCategory.StateMachine,
            $"{deviceName} 握手位复位 {cleared}/{points.Count}：{string.Join('、', points)}");
        return cleared;
    }
}
