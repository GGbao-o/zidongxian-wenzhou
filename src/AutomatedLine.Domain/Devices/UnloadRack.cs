namespace AutomatedLine.Domain.Devices;

/// <summary>
/// 下料架。仅下料。
/// </summary>
public sealed class UnloadRack : DeviceBase
{
    /// <summary>构造函数。</summary>
    /// <param name="id">设备号</param>
    /// <param name="name">设备名称</param>
    /// <param name="positionX">物理 X 位置</param>
    public UnloadRack(int id, string name, double positionX)
        : base(id, name, DeviceType.UnloadRack, positionX)
    {
    }
}
