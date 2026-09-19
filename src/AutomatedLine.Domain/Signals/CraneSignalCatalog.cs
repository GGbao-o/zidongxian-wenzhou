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
        new("Magnetized", SignalDataType.Bool, SignalDirection.Read, "充磁状态"),
        new("HasPlate", SignalDataType.Bool, SignalDirection.Read, "有板"),
        new("PressDownState", SignalDataType.Bool, SignalDirection.Read, "下压到位"),

        // 自动流程下发类
        new("TargetX", SignalDataType.Double, SignalDirection.Write, "目标 X"),
        new("TargetY", SignalDataType.Double, SignalDirection.Write, "目标 Y"),
        new("TargetZ", SignalDataType.Double, SignalDirection.Write, "目标 Z"),
        new("ActionWord", SignalDataType.Int, SignalDirection.Write, "动作字"),
        new("PickRequest", SignalDataType.Bool, SignalDirection.Write, "取料请求"),
        new("PlaceRequest", SignalDataType.Bool, SignalDirection.Write, "放料请求"),
        new("Grip", SignalDataType.Bool, SignalDirection.Write, "抓取/松开"),

        // 手动操作下发类（手动点动/绝对运动/清报警/充放磁/下压）
        new("MoveStart", SignalDataType.Bool, SignalDirection.Write, "启动运动"),
        new("MoveMode", SignalDataType.Int, SignalDirection.Write, "运动模式（0 绝对 / 1 相对）"),
        new("MoveSpeedX", SignalDataType.Double, SignalDirection.Write, "X 轴速度"),
        new("MoveAccelX", SignalDataType.Double, SignalDirection.Write, "X 轴加速度"),
        new("MoveDecelX", SignalDataType.Double, SignalDirection.Write, "X 轴减速度"),
        new("MoveSpeedY", SignalDataType.Double, SignalDirection.Write, "Y 轴速度"),
        new("MoveAccelY", SignalDataType.Double, SignalDirection.Write, "Y 轴加速度"),
        new("MoveDecelY", SignalDataType.Double, SignalDirection.Write, "Y 轴减速度"),
        new("MoveSpeedZ", SignalDataType.Double, SignalDirection.Write, "Z 轴速度"),
        new("MoveAccelZ", SignalDataType.Double, SignalDirection.Write, "Z 轴加速度"),
        new("MoveDecelZ", SignalDataType.Double, SignalDirection.Write, "Z 轴减速度"),
        new("ClearAlarm", SignalDataType.Bool, SignalDirection.Write, "清除报警"),
        new("Magnetize", SignalDataType.Bool, SignalDirection.Write, "充磁"),
        new("Demagnetize", SignalDataType.Bool, SignalDirection.Write, "放磁"),
        new("PressDown", SignalDataType.Bool, SignalDirection.Write, "下压")
    };
}
