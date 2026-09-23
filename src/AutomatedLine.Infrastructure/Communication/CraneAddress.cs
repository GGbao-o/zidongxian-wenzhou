namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 天车寄存器地址常量（汇川 PLC / Modbus TCP）。
/// 地址为 Modbus 线上地址（0 基），与现场 D 编号一致（D5000 → 5000）。
/// X 区为**八进制编号**：线圈地址 = 63488 + 物理序号（X11=63497）。
/// 仅列实际使用到的寄存器。
/// </summary>
public static class CraneAddress
{
    // ── 状态读取：D5000~D5029（FC03 批量读 30 字）──
    /// <summary>状态批量读起始地址 D5000。</summary>
    public const int StatusStart = 5000;

    /// <summary>状态批量读寄存器数量。</summary>
    public const int StatusCount = 30;

    /// <summary>D5006 X 轴已回原点。</summary>
    public const int XHomeCompleted = 5006;

    /// <summary>D5007 Y 轴已回原点。</summary>
    public const int YHomeCompleted = 5007;

    /// <summary>D5008 Z 轴已回原点。</summary>
    public const int ZHomeCompleted = 5008;

    /// <summary>D5011 伺服报警。</summary>
    public const int ServoAlarm = 5011;

    /// <summary>D5012 PLC 伺服指令报警。</summary>
    public const int PlcServoAlarm = 5012;

    /// <summary>D5013 PLC 报警。</summary>
    public const int PlcAlarm = 5013;

    /// <summary>D5014~D5015 X 绝对编码器值（DINT）。</summary>
    public const int XEncoder = 5014;

    /// <summary>D5018~D5019 X 显示值坐标（DINT）。</summary>
    public const int XPosition = 5018;

    /// <summary>D5020 Y 绝对编码器值。</summary>
    public const int YEncoder = 5020;

    /// <summary>D5022 Y 显示值坐标。</summary>
    public const int YPosition = 5022;

    /// <summary>D5023 Z 绝对编码器值。</summary>
    public const int ZEncoder = 5023;

    /// <summary>D5025 Z 显示值坐标。</summary>
    public const int ZPosition = 5025;

    /// <summary>D5029 有版。</summary>
    public const int HasRoller = 5029;

    // ── X 区输入（FC01 读线圈，八进制编号）──
    /// <summary>X0 接液盘门开到位。</summary>
    public const int X0_DrainOpen = 63488;

    /// <summary>X1 接液盘门关到位。</summary>
    public const int X1_DrainClose = 63489;

    /// <summary>X2 磁铁下压限位。</summary>
    public const int X2_MagnetDownLimit = 63490;

    /// <summary>X3 X 轴物理原点输入。</summary>
    public const int X3_HomeX = 63491;

    /// <summary>X4 Y 轴物理原点输入。</summary>
    public const int X4_HomeY = 63492;

    /// <summary>X5 Z 轴物理原点输入。</summary>
    public const int X5_HomeZ = 63493;

    /// <summary>X6 充磁反馈。</summary>
    public const int X6_MagnetizeOk = 63494;

    /// <summary>X7 退磁反馈。</summary>
    public const int X7_DemagnetizeOk = 63495;

    /// <summary>X11 检测有版。</summary>
    public const int X11_HasPlate = 63497;

    // ── 速度/加减速：D2501~D2527（FC06）──
    /// <summary>X 绝对速度。</summary>
    public const int XAbsSpeed = 2501;
    /// <summary>X 绝对加速度。</summary>
    public const int XAbsAccel = 2502;
    /// <summary>X 绝对减速度。</summary>
    public const int XAbsDecel = 2503;
    /// <summary>Y 绝对速度。</summary>
    public const int YAbsSpeed = 2510;
    /// <summary>Y 绝对加速度。</summary>
    public const int YAbsAccel = 2511;
    /// <summary>Y 绝对减速度。</summary>
    public const int YAbsDecel = 2512;
    /// <summary>Z 绝对速度。</summary>
    public const int ZAbsSpeed = 2519;
    /// <summary>Z 绝对加速度。</summary>
    public const int ZAbsAccel = 2520;
    /// <summary>Z 绝对减速度。</summary>
    public const int ZAbsDecel = 2521;

    // ── 绝对目标：D3102~D3107（FC16，DINT 小端低字在前）──
    /// <summary>X 绝对目标（D3102~D3103）。</summary>
    public const int XAbsTarget = 3102;
    /// <summary>Y 绝对目标（D3104~D3105）。</summary>
    public const int YAbsTarget = 3104;
    /// <summary>Z 绝对目标（D3106~D3107）。</summary>
    public const int ZAbsTarget = 3106;

    // ── 自动/手动模式 ──
    /// <summary>D4001 自动/手动模式（2=手动）。</summary>
    public const int ModeCmd = 4001;

    // ── 手动操作区：D4500~D4523（FC06）──
    /// <summary>D4500 手动操作使能（2）。</summary>
    public const int ManualMode = 4500;

    /// <summary>D4501 设置相对运动距离。</summary>
    public const int ManualDistance = 4501;

    /// <summary>D4502 相对运动方向（1=正，2=负）。</summary>
    public const int ManualDirection = 4502;

    /// <summary>D4503 手动 Z 相对运动。</summary>
    public const int ManualMoveZ = 4503;

    /// <summary>D4504 手动 Y 相对运动。</summary>
    public const int ManualMoveY = 4504;

    /// <summary>D4505 手动 X 相对运动。</summary>
    public const int ManualMoveX = 4505;

    /// <summary>D4506 手动 Z 回原点。</summary>
    public const int ManualHomeZ = 4506;

    /// <summary>D4507 手动 Y 回原点。</summary>
    public const int ManualHomeY = 4507;

    /// <summary>D4508 手动 X 回原点。</summary>
    public const int ManualHomeX = 4508;

    /// <summary>D4510 手动充磁。</summary>
    public const int ManualMagnetOn = 4510;

    /// <summary>D4511 手动退磁。</summary>
    public const int ManualMagnetOff = 4511;

    /// <summary>D4512 手动开接液盘。</summary>
    public const int ManualDrainOpen = 4512;

    /// <summary>D4513 手动关接液盘。</summary>
    public const int ManualDrainClose = 4513;

    /// <summary>D4514 手动清除报警。</summary>
    public const int ManualClearAlarm = 4514;

    /// <summary>D4515 手动伺服断电。</summary>
    public const int ManualServoPowerOff = 4515;

    /// <summary>D4516 手动伺服通电。</summary>
    public const int ManualServoPowerOn = 4516;

    /// <summary>D4517 减速停止。</summary>
    public const int ManualSlowStop = 4517;

    /// <summary>D4518 急停伺服运动。</summary>
    public const int ManualEStop = 4518;

    /// <summary>D4520 手动 Z 绝对移动触发/复位。</summary>
    public const int ManualZAbsMove = 4520;

    /// <summary>D4521 手动 Y 绝对移动触发/复位。</summary>
    public const int ManualYAbsMove = 4521;

    /// <summary>D4522 手动 X 绝对移动触发/复位。</summary>
    public const int ManualXAbsMove = 4522;

    /// <summary>D4523 下压急停触发/复位。</summary>
    public const int PressureEStop = 4523;
}
