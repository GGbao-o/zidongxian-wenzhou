namespace AutomatedLine.Domain.Devices;

/// <summary>
/// 上料架。仅上料，无加工工序。
/// </summary>
public sealed class LoadRack : DeviceBase
{
    /// <summary>构造函数。</summary>
    /// <param name="id">设备号</param>
    /// <param name="name">设备名称</param>
    /// <param name="positionX">物理 X 位置</param>
    public LoadRack(int id, string name, double positionX)
        : base(id, name, DeviceType.LoadRack, positionX)
    {
    }
}
