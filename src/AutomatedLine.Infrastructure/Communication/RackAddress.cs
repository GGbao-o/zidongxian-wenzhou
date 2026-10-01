namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 料架（上料架/下料架）寄存器地址（Modbus TCP）。
/// 端口 <b>10000</b>、站号 <b>254</b> 写死；IP 用数据库。
/// 上料架与下料架同一 IP、同一采集器，仅地址不同。
/// 读：FC02 输入线圈（1x 基，10001→0 基 0）；写：FC05 输出线圈（当前不需要写）。
/// </summary>
public static class RackAddress
{
    /// <summary>TCP 端口（写死，非标准 502）。</summary>
    public const int Port = 10000;

    /// <summary>Modbus 站号（写死）。</summary>
    public const byte UnitId = 254;

    // ── 输入线圈（读，FC02）──
    /// <summary>上料架有板信号（输入线圈 10001 → 0 基 0，bit0）。</summary>
    public const int LoadRackHasPlate = 0;

    /// <summary>下料架有板信号（输入线圈 10002 → 0 基 1，bit1）。</summary>
    public const int UnloadRackHasPlate = 1;

    /// <summary>输入线圈批量读取起始地址（0）。</summary>
    public const int InputStart = 0;

    /// <summary>输入线圈批量读取数量（2：上料架、下料架）。</summary>
    public const int InputCount = 2;

    // ── 输出线圈（写，FC05；当前上位机不写，仅保留定义）──
    /// <summary>
    /// 下料架放料完成脉冲（输出线圈 OUT2）。
    /// 文档同时给"线圈 1"与"00002（0x 基）"两种写法，取值待现场确认；当前不需要写。
    /// </summary>
    public const int UnloadPlaceDoneCoil = 1;
}
