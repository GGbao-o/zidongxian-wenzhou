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

    /// <summary>设备协议明确支持的 FC04 连续输入区；未指定时保持原逐点读取。</summary>
    private readonly (int Start, int Count)? _inputRegisterBlock;

    /// <summary>工厂仅为旧打号机启用FC04响应尾零适配，正常读写继续复用连接。</summary>
    private readonly bool _expectMarkerReadTrailingZero;

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
    /// <param name="inputRegisterBlock">可选的 FC04 连续输入区，打号机使用起点0、数量4；其他设备不指定</param>
    /// <param name="expectMarkerReadTrailingZero">是否适配FC04正常响应末尾额外的一个00，默认false，仅旧打号机启用</param>
    public ModbusDeviceDriver(int deviceId, string ip, int port, byte unitId, IReadOnlyList<ModbusPoint> points, IAppLogger logger, int timeoutMs = 3000,
        (int Start, int Count)? inputRegisterBlock = null, bool expectMarkerReadTrailingZero = false)
    {
        DeviceId = deviceId;
        _points = points;
        _logger = logger;
        _inputRegisterBlock = inputRegisterBlock;
        _expectMarkerReadTrailingZero = expectMarkerReadTrailingZero;
        _client = new ModbusTcpClient(ip, port, unitId, timeoutMs, logger, expectMarkerReadTrailingZero);
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
        // 打号机与现场工具保持同一请求形态：FC04(0,4)，四个状态来自同一次响应，失败不返回半份快照。
        ushort[]? inputRegisters = null;
        if (_inputRegisterBlock is { } block)
        {
            inputRegisters = await _client.ReadInputRegistersAsync(block.Start, block.Count, cancellationToken).ConfigureAwait(false);
            if (_expectMarkerReadTrailingZero)
            {
                // 只在驱动返回后记录，说明尾字节已实际消费；保持抽样节流，不为500ms轮询逐次刷日志。
                _logger.Sample(LogCategory.Communication, $"marker-read-trailer-{DeviceId}",
                    $"设备[{DeviceId}] FC04尾字节适配采集完成：起始地址={block.Start}，数量={block.Count}，已消费并校验尾字节00，保留连接复用");
            }
        }

        foreach (var point in _points)
        {
            if (point.Writable)
            {
                continue;
            }

            if (inputRegisters is not null && _inputRegisterBlock is { } range
                && point.Area == ModbusArea.InputRegister
                && point.Address >= range.Start && point.Address - range.Start < range.Count)
            {
                var raw = inputRegisters[point.Address - range.Start];
                result[point.Name] = point.IsBool ? raw != 0 : (object)(raw / point.Scale);
            }
            else
            {
                result[point.Name] = await ReadPointAsync(point, cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task WriteSignalAsync(string pointName, object? value, CancellationToken cancellationToken = default)
    {
        // 写响应按标准长度处理；仅在收到应答后记录成功，不把“已发送”当成功。
        var connectionSuffix = _expectMarkerReadTrailingZero ? "（打号机长连接：应答已接收）" : string.Empty;
        // 旧打号机直接UTF8解码请求数据，组Word时必须保留线上字节原序，不能逐对交换。
        if (pointName == "PrintContent")
        {
            var text = value?.ToString() ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(text);
            // 每次FC16都会替换整段打印内容，不能拆包；空内容也不能生成数量为0的请求。
            const int maxPrintBytes = 123 * 2;
            if (bytes.Length == 0 || bytes.Length > maxPrintBytes)
            {
                throw new ArgumentException($"打印内容UTF-8字节数必须为1～{maxPrintBytes}，当前={bytes.Length}；不允许截断或分段下发", nameof(value));
            }

            var paddingRequired = (bytes.Length & 1) != 0;
            var registers = new ushort[(bytes.Length + 1) / 2];
            for (var i = 0; i < registers.Length; i++)
            {
                // 服务端不裁掉NUL，奇数末尾补可打印空格，第一字节放Word高8位。
                var offset = i * 2;
                var secondByte = offset + 1 < bytes.Length ? bytes[offset + 1] : (byte)0x20;
                registers[i] = (ushort)((bytes[offset] << 8) | secondByte);
            }

            _logger.Info(LogCategory.Communication,
                $"设备[{DeviceId}] FC16写入开始：PrintContent，PDU={MarkerMachineAddress.PrintContentStart}，内容=「{text.Replace("\r", "\\r").Replace("\n", "\\n")}」，UTF8字节数={bytes.Length}，发送字节数={registers.Length * 2}，填充={(paddingRequired ? "末尾空格0x20" : "无")}，Word数量={registers.Length}，高字节在前组字=[{string.Join(",", registers.Select(word => $"0x{word:X4}"))}]；等待应答");
            await _client.WriteRegistersAsync(MarkerMachineAddress.PrintContentStart, registers, cancellationToken).ConfigureAwait(false);
            _logger.Info(LogCategory.Communication, $"设备[{DeviceId}] 写打印内容 {bytes.Length} 字节，发送{registers.Length * 2}字节，{registers.Length}个Word，应答已接收{connectionSuffix}");
            return;
        }

        var point = _points.FirstOrDefault(p => p.Name == pointName && p.Writable)
                    ?? throw new NotSupportedException($"设备[{DeviceId}] 不支持写点名：{pointName}");

        switch (point.Area)
        {
            case ModbusArea.Coil:
                _logger.Info(LogCategory.Communication, $"设备[{DeviceId}] FC05写入开始：{point.Name}，PDU={point.Address}，录入={value}；等待应答");
                await _client.WriteCoilAsync(point.Address, ToBool(value), cancellationToken).ConfigureAwait(false);
                _logger.Info(LogCategory.Communication, $"设备[{DeviceId}] FC05写入完成：{point.Name}，PDU={point.Address}，应答已接收");
                break;
            case ModbusArea.HoldingRegister:
                var raw = ToRegisterWord(point, value);
                _logger.Info(LogCategory.Communication,
                    $"设备[{DeviceId}] FC06写入开始：{point.Name}，PDU={point.Address}，录入={value}，倍率={point.Scale}，布尔语义={point.IsBool}，Word={raw}(0x{raw:X4})；等待应答");
                await _client.WriteRegisterAsync(point.Address, raw, cancellationToken).ConfigureAwait(false);
                _logger.Info(LogCategory.Communication, $"设备[{DeviceId}] FC06写入完成：{point.Name}，PDU={point.Address}，Word={raw}，应答已接收");
                if (point.RoundViaInt32)
                {
                    _logger.Info(LogCategory.Communication,
                        $"设备[{DeviceId}] FC06写 {point.Name}，PDU={point.Address}，录入={value}，倍率={point.Scale}，Word={raw} 成功{connectionSuffix}");
                    return;
                }
                break;
            default:
                throw new NotSupportedException($"设备[{DeviceId}] 点名 {pointName} 所在区域不可写");
        }

        _logger.Info(LogCategory.Communication, $"设备[{DeviceId}] 写 {pointName}={value}{connectionSuffix}");
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
