using System.Text;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Infrastructure.Communication;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>Modbus 区域。</summary>
public enum ModbusArea
{
    /// <summary>线圈（FC01 读 / FC05 写）。</summary>
    Coil,

    /// <summary>离散输入（FC02 读，只读）。</summary>
    DiscreteInput,

    /// <summary>保持寄存器（FC03 读 / FC06 写）。</summary>
    HoldingRegister,

    /// <summary>输入寄存器（FC04 读，只读）。</summary>
    InputRegister
}

/// <summary>
/// 一个 Modbus 逻辑点位定义。
/// </summary>
/// <param name="Name">逻辑点名</param>
/// <param name="Area">区域</param>
/// <param name="Address">0 基 PDU 地址</param>
/// <param name="Writable">是否可写</param>
/// <param name="IsBool">是否为布尔语义（寄存器按 0/1 判定）</param>
/// <param name="Scale">工程值→原始值倍率（如 ×100 填 100；布尔/整数填 1）</param>
/// <param name="InvertDiscreteInput">是否反转FC02离散输入；下料架允许放板信号需转换为HasMaterial，其他点默认不反转</param>
/// <param name="RoundViaInt32">是否按协议先Convert.ToInt32再转Word；双头镗三参数启用，其他设备保持原换算</param>
public sealed record ModbusPoint(string Name, ModbusArea Area, int Address, bool Writable, bool IsBool, double Scale = 1,
    bool InvertDiscreteInput = false, bool RoundViaInt32 = false);

/// <summary>
/// 通用 Modbus 设备驱动（双头镗/研磨机/打号机/料架共用）。
/// 由点位表驱动读（<see cref="ReadAllSignalsAsync"/> 连续轮询）与写（<see cref="WriteSignalAsync"/> 按点名）。
/// 端口/站号写死，IP 来自数据库。
/// </summary>
public sealed class ModbusDeviceDriver : IDeviceDriver
{
    /// <summary>底层 Modbus 客户端。</summary>
    private readonly ModbusTcpClient _client;

    /// <summary>点位表。</summary>
    private readonly IReadOnlyList<ModbusPoint> _points;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="deviceId">设备号</param>
    /// <param name="ip">设备 IP（来自数据库）</param>
    /// <param name="port">端口（写死）</param>
    /// <param name="unitId">站号（写死/按规则）</param>
    /// <param name="points">点位表</param>
    /// <param name="logger">日志</param>
    /// <param name="timeoutMs">单次请求/连接超时（毫秒），来自配置 Collection.CommunicationTimeoutMs</param>
    public ModbusDeviceDriver(int deviceId, string ip, int port, byte unitId, IReadOnlyList<ModbusPoint> points, IAppLogger logger, int timeoutMs = 3000)
    {
        DeviceId = deviceId;
        _points = points;
        _logger = logger;
        _client = new ModbusTcpClient(ip, port, unitId, timeoutMs);
    }

    /// <inheritdoc />
    public int DeviceId { get; }

    /// <inheritdoc />
    public bool IsConnected => _client.IsConnected;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _client.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _client.DisconnectAsync();

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken cancellationToken = default)
    {
        // 只读**输入信号**（机床→上位机，均为 1/0 或状态字）；下发的加工参数与握手输出位只写不读。
        var result = new Dictionary<string, object?>();
        foreach (var point in _points)
        {
            if (point.Writable)
            {
                continue;
            }

            result[point.Name] = await ReadPointAsync(point, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task WriteSignalAsync(string pointName, object? value, CancellationToken cancellationToken = default)
    {
        // 打号机打印内容：字节流按 FC16 写多寄存器
        if (pointName == "PrintContent")
        {
            var text = value?.ToString() ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(text);
            var registers = new ushort[(bytes.Length + 1) / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if ((i & 1) == 0)
                {
                    registers[i / 2] = bytes[i];
                }
                else
                {
                    registers[i / 2] |= (ushort)(bytes[i] << 8);
                }
            }

            await _client.WriteRegistersAsync(MarkerMachineAddress.PrintContentStart, registers, cancellationToken).ConfigureAwait(false);
            _logger.Info(LogCategory.Communication, $"设备[{DeviceId}] 写打印内容 {bytes.Length} 字节");
            return;
        }

        var point = _points.FirstOrDefault(p => p.Name == pointName && p.Writable)
                    ?? throw new NotSupportedException($"设备[{DeviceId}] 不支持写点名：{pointName}");

        switch (point.Area)
        {
            case ModbusArea.Coil:
                await _client.WriteCoilAsync(point.Address, ToBool(value), cancellationToken).ConfigureAwait(false);
                break;
            case ModbusArea.HoldingRegister:
                var raw = ToRegisterWord(point, value);
                await _client.WriteRegisterAsync(point.Address, raw, cancellationToken).ConfigureAwait(false);
                if (point.RoundViaInt32)
                {
                    _logger.Info(LogCategory.Communication,
                        $"设备[{DeviceId}] FC06写 {point.Name}，PDU={point.Address}，录入={value}，倍率={point.Scale}，Word={raw} 成功");
                    return;
                }
                break;
            default:
                throw new NotSupportedException($"设备[{DeviceId}] 点名 {pointName} 所在区域不可写");
        }

        _logger.Info(LogCategory.Communication, $"设备[{DeviceId}] 写 {pointName}={value}");
    }

    /// <summary>按点位读取单个值。</summary>
    private async Task<object?> ReadPointAsync(ModbusPoint point, CancellationToken cancellationToken)
    {
        switch (point.Area)
        {
            case ModbusArea.Coil:
                return await _client.ReadCoilAsync(point.Address, cancellationToken).ConfigureAwait(false);
            case ModbusArea.DiscreteInput:
                var bits = await _client.ReadDiscreteInputsAsync(point.Address, 1, cancellationToken).ConfigureAwait(false);
                // 仅在读取成功后按点位极性归一化；通信异常继续传播，不能把失败当成无板可放。
                return point.InvertDiscreteInput ? !bits[0] : bits[0];
            case ModbusArea.InputRegister:
                var input = await _client.ReadInputRegistersAsync(point.Address, 1, cancellationToken).ConfigureAwait(false);
                return point.IsBool ? input[0] != 0 : (object)(input[0] / point.Scale);
            case ModbusArea.HoldingRegister:
                var holding = await _client.ReadRegistersAsync(point.Address, 1, cancellationToken).ConfigureAwait(false);
                return point.IsBool ? holding[0] != 0 : (object)(holding[0] / point.Scale);
            default:
                return null;
        }
    }

    /// <summary>按点位协议编码单Word，保留原录入值用于日志和后续工序。</summary>
    private static ushort ToRegisterWord(ModbusPoint point, object? value)
    {
        if (point.IsBool) return (ushort)(ToBool(value) ? 1 : 0);
        var scaled = ToDouble(value) * point.Scale;
        // 双头镗明确要求Convert.ToInt32（.5取偶数）后转Word；以点位选择，不改变其他设备的参数编码。
        return point.RoundViaInt32 ? (ushort)Convert.ToInt32(scaled) : (ushort)Math.Round(scaled);
    }

    /// <summary>值转布尔。</summary>
    private static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        _ => Math.Abs(ToDouble(value)) > 0.01
    };

    /// <summary>值转 double。</summary>
    private static double ToDouble(object? value) => value switch
    {
        null => 0d,
        double d => d,
        _ => Convert.ToDouble(value)
    };
}
