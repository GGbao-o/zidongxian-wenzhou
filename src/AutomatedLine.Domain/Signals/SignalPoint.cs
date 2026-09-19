namespace AutomatedLine.Domain.Signals;

/// <summary>
/// 信号点位定义。
/// 描述"逻辑点名、数据类型、读写方向、含义"；PLC 地址（寄存器号/线圈号）在点位表确定后补入，
/// 业务层只认逻辑点名，地址变化不影响业务（架构文档 §6.2、§17 不变式 8）。
/// </summary>
/// <param name="Name">逻辑点名，如 HasMaterial</param>
/// <param name="DataType">数据类型</param>
/// <param name="Direction">读写方向</param>
/// <param name="Description">中文含义</param>
public sealed record SignalPoint(string Name, SignalDataType DataType, SignalDirection Direction, string Description);
