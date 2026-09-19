using AutomatedLine.Domain.Cranes;

namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 手动操作服务：供"手动操作"页面对指定天车做三轴绝对/相对运动、设速度、清报警与充放磁/下压等动作。
/// 这些动作直接写天车 PLC 数据（现场点位到位后由驱动完成映射），用于人工调试与设定设备坐标。
/// 按现场约定：手动操作**不与自动线做互斥/排队**——自动线运行时人为不会手动操作。
/// </summary>
public interface IManualOperationService
{
    /// <summary>
    /// 绝对运动：天车移动到指定的 X/Y/Z 坐标。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="x">目标 X</param>
    /// <param name="y">目标 Y</param>
    /// <param name="z">目标 Z</param>
    /// <param name="speed">运动速度</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>命令已下发返回 true；天车不存在/未连接返回 false</returns>
    Task<bool> MoveAbsoluteAsync(int craneId, double x, double y, double z, double speed, CancellationToken cancellationToken = default);

    /// <summary>
    /// 相对运动：在指定轴方向上移动一段距离（由上位机换算成绝对目标后下发）。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="axis">轴</param>
    /// <param name="delta">相对位移，正为正向</param>
    /// <param name="speed">运动速度</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>命令已下发返回 true</returns>
    Task<bool> MoveRelativeAsync(int craneId, CraneAxis axis, double delta, double speed, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置运动速度。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="speed">速度值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>命令已下发返回 true</returns>
    Task<bool> SetSpeedAsync(int craneId, double speed, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除天车报警。
    /// </summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>命令已下发返回 true</returns>
    Task<bool> ClearAlarmAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>充磁。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>命令已下发返回 true</returns>
    Task<bool> MagnetizeAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>放磁。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>命令已下发返回 true</returns>
    Task<bool> DemagnetizeAsync(int craneId, CancellationToken cancellationToken = default);

    /// <summary>下压。</summary>
    /// <param name="craneId">天车号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>命令已下发返回 true</returns>
    Task<bool> PressDownAsync(int craneId, CancellationToken cancellationToken = default);
}
