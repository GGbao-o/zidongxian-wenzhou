namespace AutomatedLine.Domain.Signals;

/// <summary>
/// 天车信号点位目录。
/// 包括三轴坐标、到位、报警、伴信号，以及上位机下发的目标位置与动作字。
/// 点位地址待寄存器表确定后补入（架构文档 §6.3、§9.1）。
/// </summary>
public static class CraneSignalCatalog
{
    /// <summary>天车全部逻辑点位。</summary>
    public static readonly IReadOnlyList<SignalPoint> Points = new List<SignalPoint>
    {
        // 采集类
        new("X", SignalDataType.Double, SignalDirection.Read, "X 轴当前位置"),
        new("Y", SignalDataType.Double, SignalDirection.Read, "Y 轴当前位置"),
        new("Z", SignalDataType.Double, SignalDirection.Read, "Z 轴当前位置"),
        new("InPosition", SignalDataType.Bool, SignalDirection.Read, "运动到位"),
        new("Alarm", SignalDataType.Bool, SignalDirection.Read, "天车报警"),
        new("HasMaterial", SignalDataType.Bool, SignalDirection.Read, "天车有料"),
        new("CompanionSignal", SignalDataType.Int, SignalDirection.Read, "伴信号"),

        // 下发类
        new("TargetX", SignalDataType.Double, SignalDirection.Write, "目标 X"),
        new("TargetY", SignalDataType.Double, SignalDirection.Write, "目标 Y"),
        new("TargetZ", SignalDataType.Double, SignalDirection.Write, "目标 Z"),
        new("ActionWord", SignalDataType.Int, SignalDirection.Write, "动作字"),
        new("PickRequest", SignalDataType.Bool, SignalDirection.Write, "取料请求"),
        new("PlaceRequest", SignalDataType.Bool, SignalDirection.Write, "放料请求"),
        new("Grip", SignalDataType.Bool, SignalDirection.Write, "抓取/松开")
    };
}
