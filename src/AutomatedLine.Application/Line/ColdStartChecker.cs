using AutomatedLine.Application.Alarms;
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

    /// <summary>驱动注册表（天车按需读状态）。</summary>
    private readonly DriverRegistry _driverRegistry;

    /// <summary>报警管理器。</summary>
    private readonly AlarmManager _alarmManager;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="driverRegistry">驱动注册表</param>
    /// <param name="alarmManager">报警管理器</param>
    public ColdStartChecker(DeviceRegistry deviceRegistry, CraneRegistry craneRegistry, DriverRegistry driverRegistry, AlarmManager alarmManager)
    {
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _driverRegistry = driverRegistry;
        _alarmManager = alarmManager;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var found = new List<string>();

        foreach (var device in _deviceRegistry.Snapshot())
        {
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

        foreach (var crane in _craneRegistry.Snapshot())
        {
            // 天车不再后台采集：按需读一次状态，有板信号看 X11
            var driver = _driverRegistry.GetCrane(crane.Id);
            var status = driver is null ? null : await driver.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status?.HasPlate == true && crane.CurrentWorkpiece is null)
            {
                var message = $"天车[{crane.Name}] 现场有料但内存无数据，将不参与调度";
                found.Add(message);
                _alarmManager.Raise(AlarmLevel.Warning, "冷启动核对", message, "不排产，人工取走即可", crane.Id, true);
            }
        }

        return found;
    }
}
