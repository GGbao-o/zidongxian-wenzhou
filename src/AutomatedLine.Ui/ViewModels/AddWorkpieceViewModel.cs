using AutomatedLine.Domain.Workpieces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 添加工件弹窗的 ViewModel。
/// 只负责收集工件数据并组装成 <see cref="WorkpieceInfo"/>，不直接操作任务表（由主 ViewModel 决定是否加入）。
/// </summary>
public partial class AddWorkpieceViewModel : ObservableObject
{
    /// <summary>版号。</summary>
    [ObservableProperty]
    private string _plateNo = string.Empty;

    /// <summary>序号（输入用文本，组装时转 int）。</summary>
    [ObservableProperty]
    private string _serialNo = string.Empty;

    /// <summary>直径。</summary>
    [ObservableProperty]
    private double _diameter;

    /// <summary>长度。</summary>
    [ObservableProperty]
    private double _length;

    /// <summary>左堵厚。</summary>
    [ObservableProperty]
    private double _leftPlugThickness;

    /// <summary>右堵厚。</summary>
    [ObservableProperty]
    private double _rightPlugThickness;

    /// <summary>堵孔尺寸：只能 70（小孔）或 100（大孔）。</summary>
    [ObservableProperty]
    private int _plugHole;

    /// <summary>打印内容。</summary>
    [ObservableProperty]
    private string _printContent = string.Empty;

    /// <summary>内孔锥度。</summary>
    [ObservableProperty]
    private double _innerHoleTaper;

    /// <summary>圆角大小。</summary>
    [ObservableProperty]
    private double _filletSize;

    /// <summary>是否做动平衡：0=不需要、1=需要。</summary>
    [ObservableProperty]
    private int _needBalancing;

    /// <summary>
    /// 组装工件数据对象。
    /// </summary>
    /// <returns>工件数据</returns>
    public WorkpieceInfo BuildWorkpieceInfo() => new()
    {
        PlateNo = PlateNo.Trim(),
        SerialNo = int.TryParse(SerialNo.Trim(), out var serialNo) ? serialNo : 0,
        Diameter = Diameter,
        Length = Length,
        LeftPlugThickness = LeftPlugThickness,
        RightPlugThickness = RightPlugThickness,
        PlugHole = PlugHole,
        PrintContent = PrintContent.Trim(),
        InnerHoleTaper = InnerHoleTaper,
        FilletSize = FilletSize,
        NeedBalancing = NeedBalancing
    };

    /// <summary>
    /// 校验输入是否合法：版号非空、序号为正整数、直径/长度为正、堵孔只能 70/100、动平衡 0/1。
    /// </summary>
    /// <returns>合法返回 true</returns>
    public bool Validate()
    {
        return !string.IsNullOrWhiteSpace(PlateNo)
               && int.TryParse(SerialNo.Trim(), out var serialNo) && serialNo > 0
               && Diameter > 0
               && Length > 0
               && (PlugHole == 70 || PlugHole == 100)
               && (NeedBalancing == 0 || NeedBalancing == 1);
    }
}
