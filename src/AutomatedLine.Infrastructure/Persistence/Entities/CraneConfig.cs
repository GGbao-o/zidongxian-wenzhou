namespace AutomatedLine.Infrastructure.Persistence.Entities;

/// <summary>
/// 天车配置实体（映射 CraneConfig 表）。
/// 只存"基本不变的物理与连接参数"，不存运行态（运行态只存在内存，见架构文档 §3.4）。
/// 字段依据架构文档 §14.2。
/// </summary>
public class CraneConfig
{
    /// <summary>主键，同时作为天车号使用。</summary>
    public int Id { get; set; }

    /// <summary>显示序号，用于页面排序，可不同于 Id。</summary>
    public int No { get; set; }

    /// <summary>天车名称，例如"天车1"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>通信协议类型，例如 ModbusTcp / Mc / Simulated。换协议只改配置与驱动实现。</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>PLC IP 地址。</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>PLC 端口。</summary>
    public int Port { get; set; }

    /// <summary>活动范围左端 X 坐标。</summary>
    public double StartX { get; set; }

    /// <summary>活动范围右端 X 坐标。相邻天车的范围允许重叠（交叉区）。</summary>
    public double EndX { get; set; }

    /// <summary>天车物理宽度。用于计算路权区间并向两端各外扩半个宽度（架构文档 §8.7.1）。</summary>
    public double Width { get; set; }

    /// <summary>安全位 X 坐标。任务结束后回到此处，保证不占用互斥区，空闲天车不占路权。</summary>
    public double SafeX { get; set; }

    /// <summary>安全位 Z 坐标（安全高度），移动前的抬升高度。</summary>
    public double SafeZ { get; set; }

    /// <summary>X 轴偏移量。用于坐标标定/微调（实际目标 = 现场坐标 + 偏移）。</summary>
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

    /// <summary>运动到位超时（毫秒）。默认 60000（60s）。</summary>
    public int MoveTimeoutMs { get; set; } = 60_000;

    /// <summary>是否启用。停用的天车不参与调度。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    /// <summary>最后更新时间（UTC）。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
