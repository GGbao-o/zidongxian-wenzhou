using AutomatedLine.Application.Abstractions;

namespace AutomatedLine.Application.Collection;

/// <summary>一次天车采集的不可变结果；仅保存 PLC 数据与采集元信息，不保存任务、工件或路权。</summary>
public sealed class CraneStateSnapshot
{
    /// <summary>天车标识。</summary>
    public int CraneId { get; init; }

    /// <summary>全局递增采集结果序号，失败发布也递增。</summary>
    public long Sequence { get; init; }

    /// <summary>读取开始时间；多次通信组成一次状态读取，不代表所有点位在同一瞬间取值。</summary>
    public DateTimeOffset ReadStartedAt { get; init; }

    /// <summary>读取结束并发布结果的时间，用于诊断通信耗时。</summary>
    public DateTimeOffset CollectedAt { get; init; }

    /// <summary>本次状态；整次失败时为 null，单个 X 区点位失败仍由状态中的可空值表示。</summary>
    public CraneStatusDto? Status { get; init; }

    /// <summary>整次采集失败原因；成功时为 null，不能用上一次成功结果替代本次失败。</summary>
    public string? FailureReason { get; init; }
}
