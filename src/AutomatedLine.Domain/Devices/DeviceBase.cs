using AutomatedLine.Domain.Signals;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Domain.Devices;

/// <summary>
/// 设备运行时对象基类。
/// "一个物理设备 = 一个运行时对象实例"，例如 <c>LatheMachine</c> 会实例化出 Lathe01/Lathe02/Lathe03。
/// 实例由 DeviceRegistry 全局唯一持有，杜绝"UI 显示一个、任务生成器又 new 一个"的状态不同步（架构文档 §7）。
/// </summary>
public abstract class DeviceBase
{
    /// <summary>设备号，与数据库 DeviceConfig.Id 对应。</summary>
    public int Id { get; }

    /// <summary>设备名称，如"斜床01"。</summary>
    public string Name { get; set; }

    /// <summary>设备类型。由具体子类在构造时确定。</summary>
    public DeviceType Type { get; }

    /// <summary>设备物理 X 位置。用于计算搬运任务的路权区间。</summary>
    public double PositionX { get; set; }

    /// <summary>可加工最小直径。</summary>
    public double MinDiameter { get; set; }

    /// <summary>可加工最大直径。</summary>
    public double MaxDiameter { get; set; }

    /// <summary>可加工最小长度。</summary>
    public double MinLength { get; set; }

    /// <summary>可加工最大长度。</summary>
    public double MaxLength { get; set; }

    /// <summary>是否启用。停用的设备不参与调度。</summary>
    public bool IsEnabled { get; set; } = true;
//------------------------------------------------------------------------上面的都是数据库设备表的配置
    /// <summary>
    /// 最近一次采集的信号快照。由 DeviceStateCollector 整体替换写入，业务层只读。
    /// 为 null 表示尚未采集过。
    /// </summary>
    public SignalSnapshot? Signals { get; internal set; }

    /// <summary>是否已连接（依据最近快照）。</summary>
    public bool IsConnected => Signals is not null && Signals.IsConnected;

    /// <summary>
    /// 当前工件。这是**只读派生缓存**，不是权威状态：
    /// 权威状态是 <see cref="WorkpieceRuntime.CurrentLocation"/>，此处只在取放料确认时由天车状态机同步。
    /// </summary>
    public WorkpieceTask? CurrentWorkpiece { get; internal set; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="id">设备号</param>
    /// <param name="name">设备名称</param>
    /// <param name="type">设备类型</param>
    /// <param name="positionX">物理 X 位置</param>
    protected DeviceBase(int id, string name, DeviceType type, double positionX)
    {
        Id = id;
        Name = name;
        Type = type;
        PositionX = positionX;
    }

    /// <summary>
    /// 判断工件尺寸是否在本设备的加工范围内。用于候选任务的目标设备匹配（架构文档 §8.3.1）。
    /// </summary>
    /// <param name="diameter">工件直径</param>
    /// <param name="length">工件长度</param>
    /// <returns>匹配返回 true</returns>
    public bool CanProcess(double diameter, double length)
        => diameter >= MinDiameter && diameter <= MaxDiameter
           && length >= MinLength && length <= MaxLength;

    /// <summary>
    /// 便于日志/界面显示。
    /// </summary>
    /// <returns>设备名称</returns>
    public override string ToString() => Name;
}
