using AutomatedLine.Domain.Workpieces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 添加工件弹窗的 ViewModel。
/// 只负责收集工件数据并组装成 <see cref="WorkpieceInfo"/>，不直接操作任务表（由主 ViewModel 决定是否加入）。
/// </summary>
public partial class AddWorkpieceViewModel : ObservableObject
{
    /// <summary>板号。</summary>
    [ObservableProperty]
    private string _plateNo = string.Empty;

    /// <summary>序号。</summary>
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

    /// <summary>
    /// 组装工件数据对象。
    /// </summary>
    /// <returns>工件数据</returns>
    public WorkpieceInfo BuildWorkpieceInfo() => new()
    {
        PlateNo = PlateNo.Trim(),
        SerialNo = SerialNo.Trim(),
        Diameter = Diameter,
        Length = Length,
        LeftPlugThickness = LeftPlugThickness,
        RightPlugThickness = RightPlugThickness
    };

    /// <summary>
    /// 校验输入是否合法。P1 只做最基本校验：板号/序号非空、尺寸为正。
    /// </summary>
    /// <returns>合法返回 true</returns>
    public bool Validate()
    {
        return !string.IsNullOrWhiteSpace(PlateNo)
               && !string.IsNullOrWhiteSpace(SerialNo)
               && Diameter > 0
               && Length > 0;
    }
}
