namespace AutomatedLine.Domain.Devices;

/// <summary>
/// 动平衡机。
/// </summary>
public sealed class BalancingMachine : DeviceBase
{
    /// <summary>构造函数。</summary>
    /// <param name="id">设备号</param>
    /// <param name="name">设备名称</param>
    /// <param name="positionX">物理 X 位置</param>
    public BalancingMachine(int id, string name, double positionX)
        : base(id, name, DeviceType.BalancingMachine, positionX)
    {
    }
}
