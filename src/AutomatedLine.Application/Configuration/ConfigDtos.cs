using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Application.Configuration;

/// <summary>
/// 天车配置传输对象。
/// 用于界面与配置存储之间传递，避免 UI 直接依赖 Infrastructure 的 EF 实体。
/// 字段与数据字典 CraneConfig 表一致。
/// </summary>
public sealed class CraneConfigDto
{
    /// <summary>天车号（主键）。</summary>
    public int Id { get; set; }

    /// <summary>显示序号。</summary>
    public int No { get; set; }

    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>协议类型。</summary>
    public string Protocol { get; set; } = "Simulated";

    /// <summary>PLC IP。</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>PLC 端口。</summary>
    public int Port { get; set; }

    /// <summary>活动范围左端 X。</summary>
    public double StartX { get; set; }

    /// <summary>活动范围右端 X。</summary>
    public double EndX { get; set; }

    /// <summary>天车宽度。</summary>
    public double Width { get; set; }

    /// <summary>安全位 X。</summary>
    public double SafeX { get; set; }

    /// <summary>安全位 Z。</summary>
    public double SafeZ { get; set; }

    /// <summary>X 轴偏移量（坐标标定/微调）。</summary>
    public double OffsetX { get; set; }

    /// <summary>Y 轴偏移量。</summary>
    public double OffsetY { get; set; }

    /// <summary>Z 轴偏移量。</summary>
    public double OffsetZ { get; set; }

    /// <summary>X 轴速度。</summary>
    public double SpeedX { get; set; }

    /// <summary>Y 轴速度。</summary>
    public double SpeedY { get; set; }

    /// <summary>Z 轴速度。</summary>
    public double SpeedZ { get; set; }

    /// <summary>X 轴加速度。</summary>
    public double AccelX { get; set; }

    /// <summary>Y 轴加速度。</summary>
    public double AccelY { get; set; }

    /// <summary>Z 轴加速度。</summary>
    public double AccelZ { get; set; }

    /// <summary>X 轴减速度。</summary>
    public double DecelX { get; set; }

    /// <summary>Y 轴减速度。</summary>
    public double DecelY { get; set; }

    /// <summary>Z 轴减速度。</summary>
    public double DecelZ { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 设备配置传输对象。字段与数据字典 DeviceConfig 表一致。
/// </summary>
public sealed class DeviceConfigDto
{
    /// <summary>设备号（主键）。</summary>
    public int Id { get; set; }

    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>设备类型。</summary>
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;

    /// <summary>协议类型。</summary>
    public string Protocol { get; set; } = "Simulated";

    /// <summary>PLC IP。</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>PLC 端口。</summary>
    public int Port { get; set; }

    /// <summary>物理 X 位置。</summary>
    public double PositionX { get; set; }

    /// <summary>可加工最小直径。</summary>
    public double MinDiameter { get; set; }

    /// <summary>可加工最大直径。</summary>
    public double MaxDiameter { get; set; }

    /// <summary>可加工最小长度。</summary>
    public double MinLength { get; set; }

    /// <summary>可加工最大长度。</summary>
    public double MaxLength { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;
}
