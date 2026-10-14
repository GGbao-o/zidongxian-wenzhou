namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 料架（上料架/下料架）寄存器地址（Modbus TCP）。
/// 端口 <b>10000</b>、站号 <b>254</b> 写死；IP 用数据库。
/// 上料架与下料架同一采集器，DI1允许取料、DI3允许放料；下料架输入需反转为业务有料状态。
/// 读：FC02 输入线圈（1x 基，10001→0 基 0）；写：FC05 输出线圈（当前不需要写）。
/// </summary>
public static class RackAddress
{
    /// <summary>TCP 端口（写死，非标准 502）。</summary>
    public const int Port = 10000;

    /// <summary>Modbus 站号（写死）。</summary>
    public const byte UnitId = 254;

    // ── 输入线圈（读，FC02）──
    /// <summary>上料架允许取料（DI1/10001 → PDU0）：1有板可取，0无板不可取。</summary>
    public const int LoadRackHasPlate = 0;

    /// <summary>下料架允许放板（DI3/10003 → PDU2）：1无板可放，0有板不可放；不是直接的有板信号。</summary>
    public const int UnloadRackCanPlace = 2;

    /// <summary>输入线圈批量读取起始地址（0）。</summary>
    public const int InputStart = 0;

    /// <summary>备用连续读取范围长度（3，覆盖DI1到DI3）；当前驱动分别单读PDU0或PDU2，不读取中间DI2。</summary>
    public const int InputCount = 3;

    // ── 输出线圈（写，FC05；当前上位机不写，仅保留定义）──
    /// <summary>
    /// 下料架放料完成脉冲（输出线圈 OUT2）。
    /// 文档同时给"线圈 1"与"00002（0x 基）"两种写法，取值待现场确认；当前不需要写。
    /// </summary>
    public const int UnloadPlaceDoneCoil = 1;
}
