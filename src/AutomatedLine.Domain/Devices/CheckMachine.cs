namespace AutomatedLine.Domain.Devices;

/// <summary>
/// 验点机。是否上线以现场为准。
/// </summary>
public sealed class CheckMachine : DeviceBase
{
    /// <summary>构造函数。</summary>
    /// <param name="id">设备号</param>
    /// <param name="name">设备名称</param>
    /// <param name="positionX">物理 X 位置</param>
    public CheckMachine(int id, string name, double positionX)
        : base(id, name, DeviceType.CheckMachine, positionX)
    {
    }
}
