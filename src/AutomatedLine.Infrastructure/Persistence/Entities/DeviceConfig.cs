using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Infrastructure.Persistence.Entities;

/// <summary>
/// 设备配置实体（映射 DeviceConfig 表）。
/// 一个物理设备一行；同类设备（如斜床 N 台）是多行不同 Id、相同 DeviceType。
/// 哪些设备存在、有几台，全部由本表决定，程序不写死（架构文档 §3.1、§14.2）。
/// </summary>
public class DeviceConfig
{
    /// <summary>主键，同时作为设备号使用。</summary>
    public int Id { get; set; }

    /// <summary>设备名称，例如"斜床01"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>设备类型。决定实例化哪个设备类、使用哪个取放料策略。</summary>
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;

    /// <summary>通信协议类型。</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>PLC IP 地址。</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>PLC 端口。</summary>
    public int Port { get; set; }

    /// <summary>设备物理 X 位置。用于计算搬运任务的路权区间。</summary>
    public double PositionX { get; set; }

    /// <summary>可加工最小直径。与工件直径比较，不匹配的设备不进入候选目标（架构文档 §8.3.1）。</summary>
    public double MinDiameter { get; set; }

    /// <summary>可加工最大直径。</summary>
    public double MaxDiameter { get; set; }

    /// <summary>可加工最小长度。</summary>
    public double MinLength { get; set; }

    /// <summary>可加工最大长度。</summary>
    public double MaxLength { get; set; }

    /// <summary>是否启用。停用的设备不参与调度。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    /// <summary>最后更新时间（UTC）。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
