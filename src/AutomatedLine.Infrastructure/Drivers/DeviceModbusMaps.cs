using AutomatedLine.Domain.Devices;
using AutomatedLine.Infrastructure.Communication;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 各设备的 Modbus 点位映射（逻辑点名 → 区域 + 地址 + 读写 + 换算）。
/// 地址均为 0 基 PDU 地址，来自《Docs/设备寄存器地址/》。
/// 布尔信号（请求/完成/夹紧等）按寄存器 0/1 或线圈判定；加工参数按倍数换算。
/// </summary>
public static class DeviceModbusMaps
{
    /// <summary>带托料架双头镗三个握手输入/三个输出；仅下发板长、外径、堵厚三个Word，外径倍率10。</summary>
    /// <returns>点位表</returns>
    public static IReadOnlyList<ModbusPoint> Boring() => new List<ModbusPoint>
    {
        // 读：握手输入
        new("RequestData", ModbusArea.HoldingRegister, BoringMachineAddress.RequestData, false, true),
        new("RequestLoad", ModbusArea.HoldingRegister, BoringMachineAddress.RequestLoad, false, true),
        new("RequestUnload", ModbusArea.HoldingRegister, BoringMachineAddress.RequestUnload, false, true),
        // 写：加工参数
        new("Length", ModbusArea.HoldingRegister, BoringMachineAddress.Length, true, false, 1, RoundViaInt32: true),
        new("Diameter", ModbusArea.HoldingRegister, BoringMachineAddress.Diameter, true, false, 10, RoundViaInt32: true),
        new("PlugThickness", ModbusArea.HoldingRegister, BoringMachineAddress.PlugThickness, true, false, 1, RoundViaInt32: true),
        // 写：握手输出
        new("DataSentDone", ModbusArea.HoldingRegister, BoringMachineAddress.DataSentDone, true, true),
        new("LoadDone", ModbusArea.HoldingRegister, BoringMachineAddress.LoadDone, true, true),
        new("UnloadDone", ModbusArea.HoldingRegister, BoringMachineAddress.UnloadDone, true, true)
    };

    /// <summary>研磨机点位表（保持寄存器）。</summary>
    /// <returns>点位表</returns>
    public static IReadOnlyList<ModbusPoint> Grinder() => new List<ModbusPoint>
    {
        // 读：输入信号
        new("RequestData", ModbusArea.HoldingRegister, GrinderMachineAddress.RequestData, false, true),
        new("RequestLoad", ModbusArea.HoldingRegister, GrinderMachineAddress.RequestLoad, false, true),
        new("LoadLockDone", ModbusArea.HoldingRegister, GrinderMachineAddress.LoadLockDone, false, true),
        new("RequestUnload", ModbusArea.HoldingRegister, GrinderMachineAddress.RequestUnload, false, true),
        new("UnloadReleaseDone", ModbusArea.HoldingRegister, GrinderMachineAddress.UnloadReleaseDone, false, true),
        new("Machining", ModbusArea.HoldingRegister, GrinderMachineAddress.Machining, false, true),
        new("SafetyDoor", ModbusArea.HoldingRegister, GrinderMachineAddress.SafetyDoor, false, true),
        new("RunState", ModbusArea.HoldingRegister, GrinderMachineAddress.RunState, false, false, 1),
        // 写：输出信号
        new("DataTransDone", ModbusArea.HoldingRegister, GrinderMachineAddress.DataTransDone, true, true),
        new("LoadInPlace", ModbusArea.HoldingRegister, GrinderMachineAddress.LoadInPlace, true, true),
        new("LoadDone", ModbusArea.HoldingRegister, GrinderMachineAddress.LoadDone, true, true),
        new("UnloadInPlace", ModbusArea.HoldingRegister, GrinderMachineAddress.UnloadInPlace, true, true),
        new("UnloadDone", ModbusArea.HoldingRegister, GrinderMachineAddress.UnloadDone, true, true),
        new("RollerDiameter", ModbusArea.HoldingRegister, GrinderMachineAddress.RollerDiameter, true, false, 1),
        new("MachiningMode", ModbusArea.HoldingRegister, GrinderMachineAddress.MachiningMode, true, false, 1),
        new("RollerLength", ModbusArea.HoldingRegister, GrinderMachineAddress.RollerLength, true, false, 1)
    };

    /// <summary>打号机点位表（读 FC04 输入寄存器、写 FC06 保持寄存器；打印内容见驱动特例）。</summary>
    /// <returns>点位表</returns>
    public static IReadOnlyList<ModbusPoint> Marker() => new List<ModbusPoint>
    {
        // 读：输入寄存器
        new("Busy", ModbusArea.InputRegister, MarkerMachineAddress.Busy, false, true),
        new("Running", ModbusArea.InputRegister, MarkerMachineAddress.Running, false, true),
        new("ProcessDone", ModbusArea.InputRegister, MarkerMachineAddress.ProcessDone, false, true),
        new("NewPrintContent", ModbusArea.InputRegister, MarkerMachineAddress.NewPrintContent, false, true),
        // 写：保持寄存器
        new("LoadDone", ModbusArea.HoldingRegister, MarkerMachineAddress.LoadDone, true, true),
        new("UnloadDone", ModbusArea.HoldingRegister, MarkerMachineAddress.UnloadDone, true, true),
        new("RollerOuterDiameter", ModbusArea.HoldingRegister, MarkerMachineAddress.RollerOuterDiameter, true, false, 1),
        new("RollerInnerDiameter", ModbusArea.HoldingRegister, MarkerMachineAddress.RollerInnerDiameter, true, false, 1)
    };

    /// <summary>料架允许输入映射为有料状态：上料架DI1同向，下料架DI3反转，保持业务层HasMaterial语义一致。</summary>
    /// <param name="type">料架类型（上料架/下料架）</param>
    /// <returns>点位表</returns>
    public static IReadOnlyList<ModbusPoint> Rack(DeviceType type)
    {
        var isUnloadRack = type == DeviceType.UnloadRack;
        var bit = isUnloadRack
            ? RackAddress.UnloadRackCanPlace
            : RackAddress.LoadRackHasPlate;

        return new List<ModbusPoint>
        {
            // 下料架原始1表示无板可放，必须变成HasMaterial=false，不能让调度/放料等待反向判断。
            new("HasMaterial", ModbusArea.DiscreteInput, bit, false, true, InvertDiscreteInput: isUnloadRack)
        };
    }
}
