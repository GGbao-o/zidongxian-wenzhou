namespace AutomatedLine.Domain.Workpieces;

/// <summary>
/// 工件自身数据（创建后不变）。
/// 只描述"这块板是什么"，不描述它现在在哪、加工到哪一步——那些属于 <see cref="WorkpieceRuntime"/>。
/// 注意：工件无条码/RFID，内存对象一旦随任务丢弃即永久丢失（架构文档 §3.5 难点 1）。
/// </summary>
public sealed class WorkpieceInfo
{
    /// <summary>运行期唯一标识。内存生成，用于关联任务与位置；不落库。</summary>
    public Guid RuntimeId { get; init; } = Guid.NewGuid();

    /// <summary>板号。</summary>
    public string PlateNo { get; init; } = string.Empty;

    /// <summary>序号。</summary>
    public string SerialNo { get; init; } = string.Empty;

    /// <summary>直径。用于目标设备加工范围匹配。</summary>
    public double Diameter { get; init; }

    /// <summary>长度。用于目标设备加工范围匹配。</summary>
    public double Length { get; init; }

    /// <summary>左堵厚。</summary>
    public double LeftPlugThickness { get; init; }

    /// <summary>右堵厚。</summary>
    public double RightPlugThickness { get; init; }

    /// <summary>
    /// 便于日志/界面显示的简要描述。
    /// </summary>
    /// <returns>形如"板号/序号"的文本</returns>
    public override string ToString() => $"{PlateNo}/{SerialNo}";
}
