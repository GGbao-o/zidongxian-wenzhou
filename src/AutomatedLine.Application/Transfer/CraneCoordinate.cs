using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 天车坐标换算（《应用层设计.md》§8.1）。
/// 绝对运动目标 = 设备数据库 X/Y/Z + 本车偏移量，Z 再减去随工件直径变化的下压量。
/// 设备坐标由调用方现查数据库后以参数传入（不缓存到领域对象）。
/// </summary>
public static class CraneCoordinate
{
    /// <summary>Z 下压经验系数 1（非斜床/研磨设备）。</summary>
    public const double ZCoef1 = 0.9537;

    /// <summary>Z 下压经验系数 2（非斜床/研磨设备）。</summary>
    public const double ZCoef2 = 0.866;

    /// <summary>设备基坐标 X = 设备 X + 本车 X 偏移。</summary>
    /// <param name="positionX">设备 X 绝对坐标（数据库现查）</param>
    /// <param name="crane">执行本任务的天车</param>
    /// <returns>X 基坐标</returns>
    public static double BaseX(double positionX, Crane crane) => positionX + crane.OffsetX;

    /// <summary>设备基坐标 Y = 设备 Y + 本车 Y 偏移。</summary>
    /// <param name="positionY">设备 Y 绝对坐标（数据库现查）</param>
    /// <param name="crane">执行本任务的天车</param>
    /// <returns>Y 基坐标</returns>
    public static double BaseY(double positionY, Crane crane) => positionY + crane.OffsetY;

    /// <summary>设备基坐标 Z = 设备 Z + 本车 Z 偏移。</summary>
    /// <param name="positionZ">设备 Z 绝对坐标（数据库现查）</param>
    /// <param name="crane">执行本任务的天车</param>
    /// <returns>Z 基坐标</returns>
    public static double BaseZ(double positionZ, Crane crane) => positionZ + crane.OffsetZ;

    /// <summary>
    /// Z 下压量：斜床/研磨 = Round(d/2)，其余 = Round((d/2)/0.9537 + (d/2)/0.866)。
    /// </summary>
    /// <param name="type">设备类型</param>
    /// <param name="diameter">当前工件直径</param>
    /// <returns>下压量（已取整）</returns>
    public static double ZDrop(DeviceType type, double diameter)
    {
        var half = diameter / 2.0;
        var drop = type is DeviceType.LatheMachine or DeviceType.GrinderMachine
            ? half
            : half / ZCoef1 + half / ZCoef2;
        return Math.Round(drop);
    }

    /// <summary>
    /// 计算某设备在此天车坐标系下的绝对运动目标（X/Y 含偏移；Z 含偏移再减下压量）。
    /// </summary>
    /// <param name="type">设备类型（决定 Z 下压公式）</param>
    /// <param name="posX">设备 X 绝对坐标</param>
    /// <param name="posY">设备 Y 绝对坐标</param>
    /// <param name="posZ">设备 Z 绝对坐标</param>
    /// <param name="crane">执行本任务的天车</param>
    /// <param name="diameter">当前工件直径</param>
    /// <returns>绝对目标 (X, Y, Z)</returns>
    public static (int X, int Y, int Z) ToAbsoluteTarget(
        DeviceType type, double posX, double posY, double posZ, Crane crane, double diameter)
    {
        var x = (int)Math.Round(BaseX(posX, crane));
        var y = (int)Math.Round(BaseY(posY, crane));
        var z = (int)Math.Round(BaseZ(posZ, crane) - ZDrop(type, diameter));
        return (x, y, z);
    }
}
