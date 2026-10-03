using AutomatedLine.Domain.Workpieces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 工件数据录入弹窗的 ViewModel（**新增**与**编辑**共用）。
/// 只负责收集工件数据并组装成 <see cref="WorkpieceInfo"/>，不直接操作任务表（由主 ViewModel 决定是否写入）。
/// 编辑模式下：**版号 / 序号 / 是否做动平衡 不可改**（只读展示），其余字段与新增一致。
/// </summary>
public partial class AddWorkpieceViewModel : ObservableObject
{
    /// <summary>
    /// 编辑模式时沿用原任务的运行期标识。
    /// 任务身份由 <c>WorkpieceInfo.RuntimeId</c> 标识（任务表/预约/天车引用都用它），
    /// 所以"改数据"必须保持身份不变，否则会变成另一块板。
    /// </summary>
    private Guid? _runtimeId;

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

    /// <summary>是否做动平衡：0=不需要、1=需要。**编辑模式下不可改**。</summary>
    [ObservableProperty]
    private int _needBalancing;

    /// <summary>斜床工艺（写入斜床宏 #909）：1~10。</summary>
    [ObservableProperty]
    private int _latheMachiningMode = WorkpieceInfo.LatheMachiningModeDefault;

    /// <summary>是否处于编辑模式（true 时身份类字段只读）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdentityEditable))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isEditMode;

    /// <summary>身份类字段（版号 / 序号 / 是否做动平衡）是否可改：**仅新增模式可改**。</summary>
    public bool IdentityEditable => !IsEditMode;

    /// <summary>弹窗标题：新增「添加工件」/ 编辑「编辑工件」。</summary>
    public string WindowTitle => IsEditMode ? "编辑工件" : "添加工件";

    /// <summary>
    /// 用已有工件数据预填（进入编辑模式）。
    /// **RuntimeId 会沿用**：改的是数据，不是身份。
    /// </summary>
    /// <param name="info">待编辑的工件数据</param>
    public void LoadFrom(WorkpieceInfo info)
    {
        _runtimeId = info.RuntimeId;
        IsEditMode = true;

        PlateNo = info.PlateNo;
        SerialNo = info.SerialNo.ToString();
        Diameter = info.Diameter;
        Length = info.Length;
        LeftPlugThickness = info.LeftPlugThickness;
        RightPlugThickness = info.RightPlugThickness;
        PlugHole = info.PlugHole;
        PrintContent = info.PrintContent;
        InnerHoleTaper = info.InnerHoleTaper;
        FilletSize = info.FilletSize;
        NeedBalancing = info.NeedBalancing;
        LatheMachiningMode = info.LatheMachiningMode;
    }

    /// <summary>
    /// 组装工件数据对象。
    /// 编辑模式沿用原 <c>RuntimeId</c>，新增模式新建一个。
    /// </summary>
    /// <returns>工件数据</returns>
    public WorkpieceInfo BuildWorkpieceInfo() => new()
    {
        RuntimeId = _runtimeId ?? Guid.NewGuid(),
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
        NeedBalancing = NeedBalancing,
        LatheMachiningMode = LatheMachiningMode
    };

    /// <summary>
    /// 校验输入是否合法：版号非空、序号为正整数、直径/长度为正、堵孔只能 70/100、
    /// 动平衡 0/1、**斜床工艺 1~10**。
    /// </summary>
    /// <returns>合法返回 true</returns>
    public bool Validate()
    {
        return !string.IsNullOrWhiteSpace(PlateNo)
               && int.TryParse(SerialNo.Trim(), out var serialNo) && serialNo > 0
               && Diameter > 0
               && Length > 0
               && (PlugHole == 70 || PlugHole == 100)
               && (NeedBalancing == 0 || NeedBalancing == 1)
               && LatheMachiningMode >= WorkpieceInfo.LatheMachiningModeMin
               && LatheMachiningMode <= WorkpieceInfo.LatheMachiningModeMax;
    }
}
