using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;

namespace AutomatedLine.Application.Line;

/// <summary>
/// 冷启动核对实现。
/// 依据最近一次采集快照判断设备/天车是否物理有料；若有料但内存中无对应工件，
/// 则标记 <see cref="ResourceState.BlockedByPhysicalState"/> 并写日志（架构文档 §10.5、§17 不变式 6）。
/// </summary>
public sealed class ColdStartChecker : IColdStartChecker
{
    /// <summary>设备注册表。</summary>
    private readonly DeviceRegistry _deviceRegistry;

    /// <summary>天车注册表。</summary>
    private readonly CraneRegistry _craneRegistry;

    /// <summary>日志。</summary>
    private readonly IAppLogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="deviceRegistry">设备注册表</param>
    /// <param name="craneRegistry">天车注册表</param>
    /// <param name="logger">日志</param>
    public ColdStartChecker(DeviceRegistry deviceRegistry, CraneRegistry craneRegistry, IAppLogger logger)
    {
        _deviceRegistry = deviceRegistry;
        _craneRegistry = craneRegistry;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Check()
    {
        var blocks = new List<string>();

        // 设备：有料信号为真但内存无对应工件 → 物理阻塞
        foreach (var device in _deviceRegistry.Snapshot())
        {
            if (device.Signals is null)
            {
                // 尚未采集到快照，无法判断，跳过（等下一周期）
                continue;
            }

            if (device.Signals.GetBool("HasMaterial") && device.CurrentWorkpiece is null)
            {
                device.ResourceState = ResourceState.BlockedByPhysicalState;
                var message = $"设备[{device.Name}] 物理有料但内存无对应任务，已标记物理阻塞";
                blocks.Add(message);
                _logger.Warn(LogCategory.Task, message);
            }
        }

        // 天车：同理
        foreach (var crane in _craneRegistry.Snapshot())
        {
            if (crane.Signals is null)
            {
                continue;
            }

            if (crane.Signals.GetBool("HasMaterial") && crane.CurrentWorkpiece is null)
            {
                crane.Runtime.State = Domain.Cranes.CraneState.Unavailable;
                var message = $"天车[{crane.Name}] 物理有料但内存无对应任务，已标记不可用";
                blocks.Add(message);
                _logger.Warn(LogCategory.Task, message);
            }
        }

        if (blocks.Count == 0)
        {
            _logger.Info(LogCategory.System, "冷启动核对通过：无物理残留");
        }

        return blocks;
    }
}
