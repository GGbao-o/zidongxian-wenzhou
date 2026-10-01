namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 双头镗寄存器地址（Modbus TCP）。
/// 端口 502、站号 1 写死；IP 用数据库。
/// 保持寄存器地址 = R 编号 × 2 + 1，下列常量均为 **0 基 PDU 地址**。
/// 加工参数为 16 位整数：长度按原值写；直径/堵厚/锥度/圆角等按 值×100 写。
/// </summary>
public static class BoringMachineAddress
{
    /// <summary>TCP 端口（写死）。</summary>
    public const int Port = 502;

    /// <summary>Modbus 站号（写死）。</summary>
    public const byte UnitId = 1;

    // ── 加工参数（写，FC06/FC16）──
    /// <summary>长度（R2041，16 位无符号，原值）。</summary>
    public const int Length = 4083;

    /// <summary>直径（R2043，值×100）。</summary>
    public const int Diameter = 4087;

    /// <summary>左堵厚（R2044，值×100）。</summary>
    public const int LeftPlugThickness = 4089;

    /// <summary>右堵厚（R2045，值×100）。</summary>
    public const int RightPlugThickness = 4091;

    /// <summary>内孔锥度（R2046，值×100）。</summary>
    public const int InnerHoleTaper = 4093;

    /// <summary>内孔成活/版孔（R2047，值×100）。</summary>
    public const int BorePlug = 4095;

    /// <summary>圆角大小（R2048，值×100）。</summary>
    public const int FilletSize = 4097;

    // ── 握手信号 ──
    /// <summary>请求数据（R6101，双头镗→上位机，读）。</summary>
    public const int RequestData = 12203;

    /// <summary>数据下发完成（R6102，上位机→双头镗，写 1）。</summary>
    public const int DataSentDone = 12205;

    /// <summary>请求上料（R6103，双头镗→上位机，读）。</summary>
    public const int RequestLoad = 12207;

    /// <summary>上料完成（R6104，上位机→双头镗，写 1）。</summary>
    public const int LoadDone = 12209;

    /// <summary>请求下料（R6107，双头镗→上位机，读）。</summary>
    public const int RequestUnload = 12215;

    /// <summary>下料完成（R6108，上位机→双头镗，写 1）。</summary>
    public const int UnloadDone = 12217;
}
