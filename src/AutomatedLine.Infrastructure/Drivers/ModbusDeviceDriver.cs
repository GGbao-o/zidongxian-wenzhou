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
public sealed record ModbusPoint(string Name, ModbusArea Area, int Address, bool Writable, bool IsBool, double Scale = 1);

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
    public ModbusDeviceDriver(int deviceId, string ip, int port, byte unitId, IReadOnlyList<ModbusPoint> points, IAppLogger logger)
    {
        DeviceId = deviceId;
        _points = points;
        _logger = logger;
        _client = new ModbusTcpClient(ip, port, unitId);
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
        var result = new Dictionary<string, object?>();
        foreach (var point in _points)
        {
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
                var raw = point.IsBool
                    ? (ushort)(ToBool(value) ? 1 : 0)
                    : (ushort)Math.Round(ToDouble(value) * point.Scale);
                await _client.WriteRegisterAsync(point.Address, raw, cancellationToken).ConfigureAwait(false);
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
                return bits[0];
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
