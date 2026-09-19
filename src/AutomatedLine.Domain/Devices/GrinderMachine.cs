namespace AutomatedLine.Domain.Devices;

/// <summary>
/// 研磨机（N 台，同类多实例）。
/// </summary>
public sealed class GrinderMachine : DeviceBase
{
    /// <summary>构造函数。</summary>
    /// <param name="id">设备号</param>
    /// <param name="name">设备名称</param>
    /// <param name="positionX">物理 X 位置</param>
    public GrinderMachine(int id, string name, double positionX)
        : base(id, name, DeviceType.GrinderMachine, positionX)
    {
    }
}
