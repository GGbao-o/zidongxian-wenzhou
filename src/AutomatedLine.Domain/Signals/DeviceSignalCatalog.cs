using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Domain.Signals;

/// <summary>
/// 设备信号点位目录。
/// P2 阶段先给出各设备类型通用的逻辑点位；点位地址待寄存器表确定后补入
/// （架构文档 §6.2：点位定义写在代码类中，不进数据库）。
/// 后续可按设备类型细分专属点位，业务层只认逻辑点名。
/// </summary>
public static class DeviceSignalCatalog
{
    /// <summary>
    /// 取料/放料确认与加工状态相关的通用点位。
    /// 与架构文档 §8.10 的确认规则对应：
    /// 取料确认需要设备允许取、设备无料；放料确认需要目标允许放、目标有料。
    /// </summary>
    private static readonly IReadOnlyList<SignalPoint> CommonPoints = new List<SignalPoint>
    {
        new("HasMaterial", SignalDataType.Bool, SignalDirection.Read, "设备有料"),
        new("Alarm", SignalDataType.Bool, SignalDirection.Read, "设备报警"),
        new("RequestLoad", SignalDataType.Bool, SignalDirection.Read, "请求上料"),
        new("RequestUnload", SignalDataType.Bool, SignalDirection.Read, "请求下料"),
        new("AllowPick", SignalDataType.Bool, SignalDirection.Read, "允许取料"),
        new("AllowPlace", SignalDataType.Bool, SignalDirection.Read, "允许放料"),
        new("ProcessDone", SignalDataType.Bool, SignalDirection.Read, "加工完成"),
        new("Clamp", SignalDataType.Bool, SignalDirection.ReadWrite, "夹紧"),
        new("Release", SignalDataType.Bool, SignalDirection.Write, "松开")
    };

    /// <summary>
    /// 获取指定设备类型的信号点位。
    /// </summary>
    /// <param name="deviceType">设备类型</param>
    /// <returns>点位列表；未知类型返回通用点位</returns>
    public static IReadOnlyList<SignalPoint> Get(DeviceType deviceType)
    {
        // P2 先对所有类型返回通用点位；后续按类型追加专属点位时在此分支即可
        return CommonPoints;
    }
}
