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

    /// <summary>版号。</summary>
    public string PlateNo { get; init; } = string.Empty;

    /// <summary>序号。</summary>
    public int SerialNo { get; init; }

    /// <summary>长度。用于目标设备加工范围匹配。</summary>
    public double Length { get; init; }

    /// <summary>直径。用于目标设备加工范围匹配。</summary>
    public double Diameter { get; init; }

    /// <summary>左堵厚。</summary>
    public double LeftPlugThickness { get; init; }

    /// <summary>右堵厚。</summary>
    public double RightPlugThickness { get; init; }

    /// <summary>堵孔尺寸：**只能 70 / 100**，70=小孔、100=大孔。</summary>
    public int PlugHole { get; init; }

    /// <summary>打印内容。</summary>
    public string PrintContent { get; init; } = string.Empty;

    /// <summary>内孔锥度。</summary>
    public double InnerHoleTaper { get; init; }

    /// <summary>圆角大小。</summary>
    public double FilletSize { get; init; }

    /// <summary>是否做动平衡：**0=不需要、1=需要**。</summary>
    public int NeedBalancing { get; init; }

    /// <summary>
    /// 便于日志/界面显示的简要描述。
    /// </summary>
    /// <returns>形如"板号/序号"的文本</returns>
    public override string ToString() => $"{PlateNo}/{SerialNo}";
}
