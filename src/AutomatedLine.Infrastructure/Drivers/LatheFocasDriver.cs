using AutomatedLine.Application.Abstractions;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Infrastructure.Communication;

namespace AutomatedLine.Infrastructure.Drivers;

/// <summary>
/// 斜床真实驱动（FANUC FOCAS，沈阳斜床）。
/// 底层经 <see cref="FocasLatheChannel"/> 在专用线程读写宏变量；本类把宏变量翻译成逻辑点名。
/// 地址见 <see cref="LatheMacroAddress"/>；IP 来自数据库，端口写死 8193。
/// </summary>
public sealed class LatheFocasDriver : IDeviceDriver
{
    /// <summary>FOCAS 通信通道（专用线程）。</summary>
    private readonly FocasLatheChannel _channel;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 后台采集的宏变量（顺序与 <see cref="ReadAllSignalsAsync"/> 一一对应）：
    /// 机床状态 #1001~#1005 + 天车握手 #1101~#1105，均为 1/0 信号；参数宏只写不读。
    /// </summary>
    private static readonly int[] SnapshotMacros =
    {
        LatheMacroAddress.RequestData,
        LatheMacroAddress.RequestLoad,
        LatheMacroAddress.TailstockClamped,
        LatheMacroAddress.RequestUnload,
        LatheMacroAddress.TailstockOpened,
        LatheMacroAddress.DataSentDone,
        LatheMacroAddress.CraneLoadInPlace,
        LatheMacroAddress.CraneLoadDone,
        LatheMacroAddress.CraneUnloadInPlace,
        LatheMacroAddress.CraneUnloadDone
    };

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="deviceId">设备号（与 DeviceConfig.Id 对应）</param>
    /// <param name="ip">CNC IP（来自数据库）</param>
    /// <param name="logger">日志</param>
    public LatheFocasDriver(int deviceId, string ip, IAppLogger logger)
    {
        DeviceId = deviceId;
        _logger = logger;
        _channel = new FocasLatheChannel(ip, logger, LatheMacroAddress.Port);
    }

    /// <inheritdoc />
    public int DeviceId { get; }

    /// <inheritdoc />
    public bool IsConnected => _channel.IsConnected;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _channel.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _channel.DisconnectAsync();

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>> ReadAllSignalsAsync(CancellationToken cancellationToken = default)
    {
        var values = await _channel.ReadMacrosAsync(SnapshotMacros, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>
        {
            ["RequestData"] = ToBool(values[0]),
            ["RequestLoad"] = ToBool(values[1]),
            ["TailstockClamped"] = ToBool(values[2]),
            ["RequestUnload"] = ToBool(values[3]),
            ["TailstockOpened"] = ToBool(values[4]),
            ["DataSentDone"] = ToBool(values[5]),
            ["CraneLoadInPlace"] = ToBool(values[6]),
            ["CraneLoadDone"] = ToBool(values[7]),
            ["CraneUnloadInPlace"] = ToBool(values[8]),
            ["CraneUnloadDone"] = ToBool(values[9])
        };
    }

    /// <inheritdoc />
    public async Task WriteSignalAsync(string pointName, object? value, CancellationToken cancellationToken = default)
    {
        var macro = pointName switch
        {
            "RollerLength" => LatheMacroAddress.RollerLength,
            "RollerDiameter" => LatheMacroAddress.RollerDiameter,
            "BorePlug" => LatheMacroAddress.BorePlug,
            "MachiningMode" => LatheMacroAddress.MachiningMode,
            "DataSentDone" => LatheMacroAddress.DataSentDone,
            "CraneLoadInPlace" => LatheMacroAddress.CraneLoadInPlace,
            "CraneLoadDone" => LatheMacroAddress.CraneLoadDone,
            "CraneUnloadInPlace" => LatheMacroAddress.CraneUnloadInPlace,
            "CraneUnloadDone" => LatheMacroAddress.CraneUnloadDone,
            "TailstockStop" => LatheMacroAddress.TailstockStop,
            _ => throw new NotSupportedException($"斜床不支持的点名：{pointName}")
        };

        double numeric = value switch
        {
            null => 0d,
            bool b => b ? 1d : 0d,
            _ => Convert.ToDouble(value)
        };

        _logger.Info(LogCategory.Communication,
            $"斜床[{DeviceId}] FOCAS写入开始：点名={pointName}，宏=#{macro}，录入={value}，下发数值={numeric}；等待SDK返回");
        await _channel.WriteMacroAsync(macro, numeric, cancellationToken).ConfigureAwait(false);
        _logger.Info(LogCategory.Communication, $"斜床[{DeviceId}] 写宏 #{macro}={numeric}");
    }

    /// <summary>宏变量浮点值转布尔（|v|&gt;0.01 视为 true）。</summary>
    private static bool ToBool(double value) => Math.Abs(value) > 0.01;
}
