namespace AutomatedLine.Application.Collection;

/// <summary>
/// 设备/天车状态采集器（架构文档 §8.2）。
/// 周期性读取所有驱动信号并整体替换到设备/天车对象上；只采集，不调度、不下发工艺动作。
/// </summary>
public interface IDeviceStateCollector
{
    /// <summary>当前采集序号（递增），用于判断快照新旧。</summary>
    long Sequence { get; }

    /// <summary>最近一次采集完成的时间；尚未采集时为 null。</summary>
    DateTime? LastCollectedAt { get; }
}
