namespace AutomatedLine.Application.Abstractions;

/// <summary>
/// 设备 PLC 通信驱动。
/// 负责读写地面设备（双头镗、斜床、研磨机等）的信号，
/// 例如"请求上料""加工完成""有料/无料""夹紧/松开"等。
/// 具体点位定义在各设备代码类中（架构文档 §6.2）。
/// </summary>
public interface IDeviceDriver : IPlcDriver
{
    /// <summary>设备号，与 DeviceConfig.Id 对应。</summary>
    int DeviceId { get; }
}
