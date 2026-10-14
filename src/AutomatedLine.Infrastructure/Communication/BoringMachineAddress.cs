namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 双头镗寄存器地址（Modbus TCP）。
/// 端口 502、站号 1 写死；IP 用数据库。
/// 保持寄存器地址 = R 编号 × 2 + 1，下列常量均为 **0 基 PDU 地址**。
/// 三个加工参数均为16位无符号Word：长度/堵厚舍入为整数，外径乘10后舍入，FC06单点写入。
/// </summary>
public static class BoringMachineAddress
{
    /// <summary>TCP 端口（写死）。</summary>
    public const int Port = 502;

    /// <summary>Modbus 站号（写死）。</summary>
    public const byte UnitId = 1;

    // ── 加工参数（写，FC06；每项只占一个Word）──
    /// <summary>板长（R150），Convert.ToInt32原值后转ushort。</summary>
    public const int Length = 301;

    /// <summary>外径（R151），Convert.ToInt32(原值×10)后转ushort，表达一位小数。</summary>
    public const int Diameter = 303;

    /// <summary>堵厚（R154），Convert.ToInt32原值后转ushort。</summary>
    public const int PlugThickness = 309;

    // ── 握手信号 ──
    /// <summary>请求数据（R6101，双头镗→上位机，读）。</summary>
    public const int RequestData = 12203;

    /// <summary>数据下发完成（R6102，上位机→双头镗，写 1）。</summary>
    public const int DataSentDone = 12205;

    /// <summary>请求上料（R6103，双头镗→上位机，读）。</summary>
    public const int RequestLoad = 12207;

    /// <summary>天车上料到位（R6104，上位机→双头镗，写1后等待夹紧）。</summary>
    public const int LoadInPlace = 12209;

    /// <summary>卡钳夹紧完成（R6105，双头镗→上位机，读1后允许退磁回升）。</summary>
    public const int LoadLockDone = 12211;

    /// <summary>天车上料退出完成（R6106，上位机→双头镗，退磁及回升成功后写1）。</summary>
    public const int LoadDone = 12213;

    /// <summary>请求下料（R6107，双头镗→上位机，读）。</summary>
    public const int RequestUnload = 12215;

    /// <summary>天车下料到位（R6108，上位机→双头镗，充磁成功后写1）。</summary>
    public const int UnloadInPlace = 12217;

    /// <summary>卡钳松开完成（R6109，双头镗→上位机，读1后允许回升）。</summary>
    public const int UnloadReleaseDone = 12219;

    /// <summary>天车下料退出完成（R6110，上位机→双头镗，回升成功后写1）。</summary>
    public const int UnloadDone = 12221;
}
