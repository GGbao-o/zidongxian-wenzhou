namespace AutomatedLine.Application.Configuration;

/// <summary>
/// 搬运流程参数（对应 appsettings 的 App:Transfer）。
/// </summary>
public sealed class TransferOptions
{
    /// <summary>每台斜床的 Y 二次偏移参数，键 = 斜床设备号。</summary>
    public Dictionary<int, LatheTransferOptions> Lathes { get; set; } = new();
}

/// <summary>
/// 单台斜床的 Y 二次偏移参数（每台一份）。
/// </summary>
public sealed class LatheTransferOptions
{
    /// <summary>该斜床当前顶尖距离。</summary>
    public double TailstockCenterDistance { get; set; }

    /// <summary>大孔孔径偏移，默认 122。</summary>
    public double BigHoleOffset { get; set; } = 122;

    /// <summary>小孔孔径偏移，默认 55。</summary>
    public double SmallHoleOffset { get; set; } = 55;
}
