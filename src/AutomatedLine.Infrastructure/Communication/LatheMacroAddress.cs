namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 沈阳斜床宏变量地址（FANUC FOCAS，端口 8193 写死，无站号；IP 用数据库）。
/// 读用 cnc_rdmacro、写用 cnc_wrmacro；下列常量为 **宏变量编号 #编号**。
/// 通信由 <c>AutomatedLine.Focas</c>（FanucSdk）完成，本类只定义地址。
/// </summary>
public static class LatheMacroAddress
{
    /// <summary>FOCAS TCP 端口（写死）。</summary>
    public const int Port = 8193;

    // ── 读：机床状态信号（机床→上位机）──
    /// <summary>#1000 机床准备就绪（0=未就绪、1=就绪）。</summary>
    public const int MachineReady = 1000;

    /// <summary>#1001 请求数据。</summary>
    public const int RequestData = 1001;

    /// <summary>#1002 请求上料。</summary>
    public const int RequestLoad = 1002;

    /// <summary>#1003 尾座夹紧到位。</summary>
    public const int TailstockClamped = 1003;

    /// <summary>#1004 请求下料。</summary>
    public const int RequestUnload = 1004;

    /// <summary>#1005 尾座分开到位。</summary>
    public const int TailstockOpened = 1005;

    /// <summary>#1006 自动门开（0=关、1=开）。</summary>
    public const int AutoDoorOpen = 1006;

    /// <summary>#1007 Y 轴安全位置。</summary>
    public const int YAxisSafePos = 1007;

    /// <summary>#1009 Z 轴安全位置。</summary>
    public const int ZAxisSafePos = 1009;

    /// <summary>#1010 刀架安全位置。</summary>
    public const int TurretSafePos = 1010;

    /// <summary>#1011 X 轴安全位置。</summary>
    public const int XAxisSafePos = 1011;

    /// <summary>#1012 机床操作模式（0=手动、1=半自动、2=全自动）。</summary>
    public const int OperationMode = 1012;

    /// <summary>#1013 刀具1使用寿命到期。</summary>
    public const int Tool1LifeEnd = 1013;

    /// <summary>#1014 刀具2使用寿命到期。</summary>
    public const int Tool2LifeEnd = 1014;

    /// <summary>#651 尾座停止（检测天车异常时停尾座）。</summary>
    public const int TailstockStop = 651;

    // ── 写：加工参数（上位机→斜床）──
    /// <summary>#800 版辊长度（mm，浮点）。</summary>
    public const int RollerLength = 800;

    /// <summary>#801 斜车成活直径（mm，浮点）。</summary>
    public const int RollerDiameter = 801;

    /// <summary>#802 堵孔（mm；70=小孔、100=大孔）。</summary>
    public const int BorePlug = 802;

    /// <summary>#909 加工模式（1/2/3）。</summary>
    public const int MachiningMode = 909;

    // ── 写：天车交互信号 ──
    /// <summary>#1101 数据下发完成（写 1）。</summary>
    public const int DataSentDone = 1101;

    /// <summary>#1102 天车上料到位。</summary>
    public const int CraneLoadInPlace = 1102;

    /// <summary>#1103 天车上料完成（写 1）。</summary>
    public const int CraneLoadDone = 1103;

    /// <summary>#1104 天车下料到位。</summary>
    public const int CraneUnloadInPlace = 1104;

    /// <summary>#1105 天车下料完成（写 1）。</summary>
    public const int CraneUnloadDone = 1105;
}
