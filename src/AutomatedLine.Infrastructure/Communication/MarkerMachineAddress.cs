namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 打号机寄存器地址（Modbus TCP）。
/// 端口 502、站号 1 写死；IP 用数据库。
/// 地址文档为 1 基编号（0001、000A…，其中含 A 的为十六进制），下列常量均已换算为 **0 基 PDU 地址**（编号 − 1）。
/// 所有状态与工艺参数均为 1 Word；打印内容为按字节流的多寄存器（FC16）。
/// </summary>
public static class MarkerMachineAddress
{
    /// <summary>TCP 端口（写死）。</summary>
    public const int Port = 502;

    /// <summary>Modbus 站号（写死）。</summary>
    public const byte UnitId = 1;

    // ── 输入寄存器（读，FC04）──
    /// <summary>
    /// 设备忙碌（0001）。**值语义：0=设备空闲、1=设备忙碌**——0 才是空闲。
    /// 因此点名刻意叫 <c>Busy</c>：使用方一律按 <c>Busy == false</c> 判断"可放料"，
    /// 避免点名写成 Idle 时被误读成"1=空闲"。
    /// </summary>
    public const int Busy = 0;

    /// <summary>设备运行（0002，0=停止、1=运行）。</summary>
    public const int Running = 1;

    /// <summary>加工完成（0003，0=未完、1=完成）。</summary>
    public const int ProcessDone = 2;

    /// <summary>设备打印文字-有新内容（0004，0=无、1=有）。</summary>
    public const int NewPrintContent = 3;

    /// <summary>输入寄存器读取起始地址（0001）与数量（4 个 Word）。</summary>
    public const int InputStart = 0;

    /// <summary>输入寄存器读取数量（4）。</summary>
    public const int InputCount = 4;

    // ── 保持寄存器（写，FC06）──
    /// <summary>上料完成（0005，写 1）。</summary>
    public const int LoadDone = 4;

    /// <summary>下料完成（0006，写 1）。</summary>
    public const int UnloadDone = 5;

    /// <summary>版辊外径（0007，单位 mm）。</summary>
    public const int RollerOuterDiameter = 6;

    /// <summary>版辊内径（0008，单位 mm）。</summary>
    public const int RollerInnerDiameter = 7;

    // ── 打印内容（写，FC16，字节流多寄存器）──
    /// <summary>打印内容起始地址（000A=十六进制 10 → 0 基 9）。</summary>
    public const int PrintContentStart = 9;

    /// <summary>打印内容结束地址（00C8=十六进制 200 → 0 基 199）。</summary>
    public const int PrintContentEnd = 199;
}
