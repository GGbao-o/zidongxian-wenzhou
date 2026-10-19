using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Runtime;
using AutomatedLine.Domain.Devices;
using AutomatedLine.Domain.Diagnostics;
using AutomatedLine.Domain.Routing;

namespace AutomatedLine.Application.Routing;

/// <summary>按最新保存的配置构建新工件工艺；模板保存与捕获串行，已启动快照不再读取设备存在性过滤。</summary>
public sealed class ProcessPlanService : IProcessPlanService
{
    private readonly object _sync = new();
    private readonly IConfigService _config;
    private readonly DeviceRegistry _devices;
    private readonly IAppLogger _logger;

    /// <summary>创建共享工艺服务。</summary>
    /// <param name="config">程序配置及局部保存入口。</param>
    /// <param name="devices">启用设备注册表，仅用于新路线构建及首次启动校验。</param>
    /// <param name="logger">工艺变更操作日志。</param>
    public ProcessPlanService(IConfigService config, DeviceRegistry devices, IAppLogger logger)
    {
        _config = config;
        _devices = devices;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProcessPlanOptions GetSettings()
    {
        lock (_sync)
            return new ProcessPlanOptions { EnabledProcesses = new(_config.GetAppOptions().ProcessPlan.EnabledProcesses) };
    }

    /// <inheritdoc />
    public ProcessPlanSnapshot Preview()
    {
        lock (_sync) return Build(GetSettings(), _devices.Snapshot(), validate: false);
    }

    /// <inheritdoc />
    public ProcessPlanSnapshot Capture()
    {
        lock (_sync) return Build(GetSettings(), _devices.Snapshot(), validate: true);
    }

    /// <inheritdoc />
    public ProcessPlanSnapshot SetEnabled(ProcessType process, bool enabled)
    {
        lock (_sync)
        {
            var node = ProcessRoute.CreateDefault().Nodes.FirstOrDefault(item => item.Process == process)
                ?? throw new ArgumentException("未知工序", nameof(process));
            if (process is ProcessType.Loading or ProcessType.Unloading)
                throw new InvalidOperationException("上料和下料是固定工序，不能取消");
            var devices = _devices.Snapshot();
            if (enabled && (!IsSupported(process) || !devices.Any(item => item.Type == node.DeviceType && item.IsEnabled)))
                throw new InvalidOperationException("该工序没有启用设备或尚未实现执行策略，不能启用");
            var options = GetSettings();
            options.EnabledProcesses[process] = enabled;
            var preview = Build(options, devices, validate: false);
            // 先保存再返回新预览；失败时配置和已有工件路线均不更新。
            _config.SaveProcessPlanOptions(options);
            _logger.Info(LogCategory.Operation, $"工艺模板已更新：{process}={(enabled ? "启用" : "跳过")}，版本={preview.Version}，路线={preview.Description}；仅后续首次启动工件生效");
            return preview;
        }
    }

    /// <summary>判断当前实现是否支持该工序的实际取放执行。</summary>
    /// <param name="process">工序。</param>
    /// <returns>已实现返回true。</returns>
    public static bool IsSupported(ProcessType process)
        => process is ProcessType.Loading or ProcessType.Boring or ProcessType.Lathing or ProcessType.Grinding
            or ProcessType.Marking or ProcessType.Unloading;
/// <summary>
/// 捕获当前最新工艺模板的快照
/// </summary>
/// <param name="options"></param>
/// <param name="devices"></param>
/// <param name="validate"></param>
/// <returns></returns>
/// <exception cref="InvalidOperationException"></exception>
    private static ProcessPlanSnapshot Build(ProcessPlanOptions options, IReadOnlyList<DeviceBase> devices, bool validate)
    {
        var hasDevice = ProcessRoute.AvailableDeviceFilter(devices);
        var nodes = new List<ProcessNode>();
        foreach (var node in ProcessRoute.CreateDefault().Nodes)
        {
            if (node.Process is ProcessType.Loading or ProcessType.Unloading)
            {
                if (validate && !hasDevice(node.DeviceType))
                    throw new InvalidOperationException($"无法启动：缺少启用的{node.DeviceType}，工艺必须包含上料和下料");
                nodes.Add(node);
                continue;
            }
            if (!options.EnabledProcesses.GetValueOrDefault(node.Process, true) || !hasDevice(node.DeviceType)) continue;
            if (!IsSupported(node.Process))
            {
                // 未实现的工序在页面灰显且不能勾选，不把它伪装为可执行路线。
                continue;
            }
            nodes.Add(node);
        }
        return new ProcessPlanSnapshot(nodes);
    }
}
