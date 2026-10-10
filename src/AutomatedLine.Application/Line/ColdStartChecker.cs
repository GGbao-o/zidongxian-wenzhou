using System.Diagnostics;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Alarms;
using AutomatedLine.Application.Collection;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Alarms;

namespace AutomatedLine.Application.Line;

/// <summary>
/// 冷启动核对实现（架构文档 §10.5）。
/// 内存任务为空，但现场某些资源显示"有料"，说明实物无法与内存任务关联。
/// 按 v1.9 约定：**仅记录报警提示，不阻塞、不要求人工点按钮清障**——
/// 该实物没有内存工件数据，TransportTaskGenerator 不会为它生成任务，人工取走即可。
/// </summary>
public sealed class ColdStartChecker : IColdStartChecker
{
    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>共享快照入口；核对本身不触发天车通信。</summary>
    private readonly ICraneStateProvider _craneStates;

    private readonly IConfigService _config;
    private readonly IAppLogger _logger;

    /// <summary>报警管理器。</summary>
    private readonly AlarmManager _alarmManager;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="craneStates">天车有效状态提供接口</param>
    /// <param name="alarmManager">报警管理器</param>
    /// <param name="config">等待首轮有效采集的期限配置</param>
    /// <param name="logger">未能取得有效快照时的核对日志</param>
    public ColdStartChecker(DeviceRegistry deviceRegistry, CraneRegistry craneRegistry, ICraneStateProvider craneStates,
        AlarmManager alarmManager, IConfigService config, IAppLogger logger)
    {
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _craneStates = craneStates;
        _alarmManager = alarmManager;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var found = new List<string>();

        foreach (var device in _deviceRegistry.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 尚未采集到快照则跳过
            if (device.Signals is null)
            {
                continue;
            }

            if (device.Signals.GetBool("HasMaterial") && device.CurrentWorkpiece is null)
            {
                var message = $"设备[{device.Name}] 现场有料但内存无数据，将不参与调度";
                found.Add(message);
                _alarmManager.Raise(AlarmLevel.Warning, "冷启动核对", message, "不排产，人工取走即可", device.Id, false);
            }
        }

        // 各车独立等候共享快照；断电车只占用自己的等待期限，不按车数累加启动等待。
        var cranes = _craneRegistry.Snapshot().Where(c => c.IsEnabled).ToList();
        var statuses = await Task.WhenAll(cranes.Select(c => WaitForPlateStatusAsync(c.Id, c.Name, cancellationToken))).ConfigureAwait(false);
        for (var i = 0; i < cranes.Count; i++)
        {
            var crane = cranes[i];
            var status = statuses[i];
            if (status?.HasPlate == true && crane.CurrentWorkpiece is null)
            {
                var message = $"天车[{crane.Name}] 现场有料但内存无数据，将不参与调度";
                found.Add(message);
                _alarmManager.Raise(AlarmLevel.Warning, "冷启动核对", message, "不排产，人工取走即可", crane.Id, true);
            }
        }

        return found;
    }

    // 启动核对必须有已知 X11；未采集、过期或该点未知都不能解释成无板。沿用仅提示的启动策略。
    private async Task<CraneStatusDto?> WaitForPlateStatusAsync(int craneId, string craneName, CancellationToken cancellationToken)
    {
        var waitMs = Math.Max(50, _config.GetAppOptions().Collection.CommunicationTimeoutMs);
        var watch = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = _craneStates.GetFreshStatus(craneId);
            if (status?.HasPlate is not null) return status;
            await Task.Delay(Math.Min(50, Math.Max(1, waitMs - (int)watch.ElapsedMilliseconds)), cancellationToken).ConfigureAwait(false);
        } while (watch.ElapsedMilliseconds < waitMs);

        _logger.Warn(AutomatedLine.Domain.Diagnostics.LogCategory.System,
            $"冷启动核对：天车[{craneName}] 在 {waitMs}ms 内未取得有效且 X11 已知的共享快照，本次该车核对未完成（不判为无板）");
        return null;
    }
}
