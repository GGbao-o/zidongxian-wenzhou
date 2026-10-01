namespace AutomatedLine.Infrastructure.Communication;

/// <summary>
/// 研磨机（新代）寄存器地址（Modbus TCP）。
/// 端口 502 写死；IP 用数据库；**站号 = IP 末段**（如 192.168.2.90 → 90）。
/// 保持寄存器地址 = R 编号 × 2 + 1，下列常量均为 **0 基 PDU 地址**。
/// </summary>
public static class GrinderMachineAddress
{
    /// <summary>TCP 端口（写死）。</summary>
    public const int Port = 502;

    /// <summary>默认站号（无法从 IP 解析时的兜底）。</summary>
    public const byte DefaultUnitId = 1;

    /// <summary>
    /// 由 IP 末段解析站号（如 192.168.2.90 → 90）。无法解析时返回 <see cref="DefaultUnitId"/>。
    /// </summary>
    /// <param name="ip">设备 IP</param>
    /// <returns>站号</returns>
    public static byte ResolveUnitId(string ip)
    {
        if (!string.IsNullOrWhiteSpace(ip))
        {
            var parts = ip.Split('.');
            if (parts.Length == 4 && byte.TryParse(parts[3], out var last))
            {
                return last;
            }
        }

        return DefaultUnitId;
    }

    // ── 输入信号（机床→上位机，读，FC03）──
    /// <summary>请求数据（R7301）。</summary>
    public const int RequestData = 14603;

    /// <summary>请求上料（R7302）。</summary>
    public const int RequestLoad = 14605;

    /// <summary>锁紧完成/上料退出（R7303）。</summary>
    public const int LoadLockDone = 14607;

    /// <summary>请求下料（R7304）。</summary>
    public const int RequestUnload = 14609;

    /// <summary>松开完成/下料退出（R7305）。</summary>
    public const int UnloadReleaseDone = 14611;

    /// <summary>加工中（R7306，0=加工完成、1=加工中）。</summary>
    public const int Machining = 14613;

    /// <summary>安全门状态（R7307，0=关、1=开）。</summary>
    public const int SafetyDoor = 14615;

    /// <summary>设备运行状态（R7308，0=空闲、1=忙碌、2=报警）。</summary>
    public const int RunState = 14617;

    // ── 输出信号（上位机→机床，写，FC06）──
    /// <summary>数据传输完成（R7311，写 1）。</summary>
    public const int DataTransDone = 14623;

    /// <summary>上料到达锁紧位置（R7312，写 1）。</summary>
    public const int LoadInPlace = 14625;

    /// <summary>上料完成（R7313，写 1）。</summary>
    public const int LoadDone = 14627;

    /// <summary>下料到达松开位置（R7314，写 1）。</summary>
    public const int UnloadInPlace = 14629;

    /// <summary>下料完成（R7315，写 1）。</summary>
    public const int UnloadDone = 14631;

    /// <summary>版辊直径（R7316，Word，mm）。</summary>
    public const int RollerDiameter = 14633;

    /// <summary>版辊加工模式（R7317，1=大孔、2=小孔）。</summary>
    public const int MachiningMode = 14635;

    /// <summary>版辊长度（R7318，Word，mm）。</summary>
    public const int RollerLength = 14637;
}
