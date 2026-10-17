using System.Collections.Concurrent;
using AutomatedLine.Application.Abstractions;
using AutomatedLine.Application.Runtime;

namespace AutomatedLine.Application.Collection;

/// <summary>全线共享的天车 PLC 快照存储，以驱动身份和单调时钟过滤重载、失败及过期数据。</summary>
public sealed class CraneStateStore : ICraneStateProvider
{
    /// <summary>按车整体替换的采集结果，包含来源驱动和读取开始的单调时间戳。</summary>
    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private readonly CraneRegistry _cranes;
    private readonly DriverRegistry _drivers;
    private readonly IConfigService _config;
    /// <summary>与采集服务共用的时间源，避免系统校时让过期结果重新有效。</summary>
    private readonly TimeProvider _time;
    /// <summary>全线采集结果序号，与业务任务号无关。</summary>
    private long _sequence;

    // 时间戳随不可变结果整体发布，避免消费者拼接到两轮不同的字段；驱动引用隔离配置重载。
    private sealed record Entry(ICraneDriver Driver, long StartedTimestamp, CraneStateSnapshot Snapshot);

    /// <summary>创建共享缓存；时钟可注入，便于确定性验证过期及系统时间调整。</summary>
    /// <param name="cranes">当前天车目录。</param>
    /// <param name="drivers">当前驱动目录。</param>
    /// <param name="config">快照有效年龄配置。</param>
    /// <param name="time">单调时钟；未指定时使用系统时钟。</param>
    public CraneStateStore(CraneRegistry cranes, DriverRegistry drivers, IConfigService config, TimeProvider? time = null)
    {
        _cranes = cranes;
        _drivers = drivers;
        _config = config;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />   获取完整采集快照，包括 PLC 状态、采集时间、成功或失败原因、序号等。
    public CraneStateSnapshot? GetSnapshot(int craneId) => FindCurrent(craneId)?.Snapshot;

    /// <inheritdoc />  只获取有效且没有过期的 PLC 状态，适合天车调度、运动控制前的状态判断   这个方法一共四个地方用 读取天车的快照
    public CraneStatusDto? GetFreshStatus(int craneId)
    {
        var entry = FindCurrent(craneId);
        if (entry is null || entry.Snapshot.Status is null) return null;

        // 从开始读取计龄：一次慢读取不能在结束时被包装成刚获取的新数据；单调时钟不受校时影响。
        var maxAge = TimeSpan.FromMilliseconds(Math.Max(50, _config.GetAppOptions().Collection.CraneSnapshotMaxAgeMs));
        return _time.GetElapsedTime(entry.StartedTimestamp) <= maxAge ? entry.Snapshot.Status : null;
    }

    /// <summary>发布本次采集结果；失败同样替换旧结果，已卸载驱动的迟到结果直接忽略。</summary>
    /// <param name="driver">实际完成本次读取的驱动。</param>
    /// <param name="status">本次状态，失败为 null。</param>
    /// <param name="startedTimestamp">开始读取时的单调时间戳，必须来自同一时钟。</param>
    /// <param name="startedAt">开始读取的展示时间。</param>
    /// <param name="failureReason">本次失败原因；成功时为 null。</param>
    public void Publish(ICraneDriver driver, CraneStatusDto? status, long startedTimestamp,
        DateTimeOffset startedAt, string? failureReason = null)
    {
        if (_cranes.Get(driver.CraneId) is not { IsEnabled: true }
            || !ReferenceEquals(_drivers.GetCrane(driver.CraneId), driver)) return;

        var snapshot = new CraneStateSnapshot
        {
            CraneId = driver.CraneId,
            Sequence = Interlocked.Increment(ref _sequence),
            ReadStartedAt = startedAt,
            CollectedAt = _time.GetUtcNow(),
            Status = status,
            FailureReason = status is null ? failureReason ?? "天车状态读取失败" : null
        };
        var candidate = new Entry(driver, startedTimestamp, snapshot);
        // 线程安全地新增或更新天车状态缓存。
        _entries.AddOrUpdate(driver.CraneId, candidate,
            (_, current) => ReferenceEquals(_drivers.GetCrane(driver.CraneId), driver) ? candidate : current);
    }

    // 每次查询核对当前归属，不让配置重载前的旧驱动结果供新天车调度使用。
    private Entry? FindCurrent(int craneId)
        => _cranes.Get(craneId) is { IsEnabled: true }
           && _entries.TryGetValue(craneId, out var entry)
           && ReferenceEquals(_drivers.GetCrane(craneId), entry.Driver) ? entry : null;
}
