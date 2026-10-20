using System.Globalization;
using AutomatedLine.Domain.Workpieces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutomatedLine.Ui.ViewModels;

/// <summary>
/// 工件数据录入弹窗的 ViewModel（**新增**与**编辑**共用）。
/// 只负责收集工件数据并组装成 <see cref="WorkpieceInfo"/>，不直接操作任务表（由主 ViewModel 决定是否写入）。
/// 编辑模式下：**版号 / 序号 不可改**（只读展示），其余字段与新增一致。
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

    /// <summary>手工录入的整数序号（允许负数、0和正数；输入用文本，确认时转int）。</summary>
    [ObservableProperty]
    private string _serialNo = string.Empty;

    /// <summary>直径原始输入；输入期间保留小数点及尾零，确认时才解析为数值。</summary>
    [ObservableProperty]
    private string _diameterText = "0";

    /// <summary>长度原始输入，避免逐字转换double吞掉尚未输入完的小数点。</summary>
    [ObservableProperty]
    private string _lengthText = "0";

    /// <summary>堵厚原始输入；确认后保留小数，双头镗的整数Word换算仍由驱动负责。</summary>
    [ObservableProperty]
    private string _plugThicknessText = "0";

    /// <summary>堵孔尺寸：只能 70（小孔）或 100（大孔）。</summary>
    [ObservableProperty]
    private int _plugHole = 70;

    /// <summary>打印内容。</summary>
    [ObservableProperty]
    private string _printContent = string.Empty;

    /// <summary>斜床工艺（写入斜床宏 #909）：0~10。</summary>
    [ObservableProperty]
    private int _latheMachiningMode = WorkpieceInfo.LatheMachiningModeDefault;

    /// <summary>是否处于编辑模式（true 时身份类字段只读）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdentityEditable))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isEditMode;

    /// <summary>身份类字段（版号 / 序号）是否可改：**仅新增模式可改**。</summary>
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
        DiameterText = info.Diameter.ToString(CultureInfo.CurrentCulture);
        LengthText = info.Length.ToString(CultureInfo.CurrentCulture);
        PlugThicknessText = info.PlugThickness.ToString(CultureInfo.CurrentCulture);
        PlugHole = info.PlugHole;
        PrintContent = info.PrintContent;
        LatheMachiningMode = info.LatheMachiningMode;
    }

    /// <summary>
    /// 组装工件数据对象。
    /// 编辑模式沿用原 <c>RuntimeId</c>，新增模式新建一个。
    /// </summary>
    /// <returns>工件数据</returns>
    /// <exception cref="InvalidOperationException">当前输入不合法，禁止使用旧数值或默认值生成工件。</exception>
    public WorkpieceInfo BuildWorkpieceInfo()
    {
        if (!TryReadInput(out var serialNo, out var diameter, out var length, out var plugThickness))
            throw new InvalidOperationException("工件输入不合法，不能生成工件数据");

        return new WorkpieceInfo
        {
            RuntimeId = _runtimeId ?? Guid.NewGuid(),
            PlateNo = PlateNo.Trim(),
            SerialNo = serialNo,
            Diameter = diameter,
            Length = length,
            PlugThickness = plugThickness,
            PlugHole = PlugHole,
            PrintContent = PrintContent.Trim(),
            LatheMachiningMode = LatheMachiningMode
        };
    }

    /// <summary>
    /// 确认时解析原始文本并校验：版号非空、序号为int整数、直径/长度为有限正数、堵厚为有限数值、堵孔70/100、斜床工艺0~10。
    /// 不回写输入文本，允许编辑中的小数点、尾零保持原样。
    /// </summary>
    /// <returns>合法返回 true</returns>
    public bool Validate() => TryReadInput(out _, out _, out _, out _);

    // 校验和组装复用同一套解析规则；错误文本不能退回上次输入成功的数值。
    private bool TryReadInput(out int serialNo, out double diameter, out double length, out double plugThickness)
    {
        var serialValid = int.TryParse(SerialNo.Trim(), out serialNo);
        var diameterValid = TryParseFinite(DiameterText, out diameter);
        var lengthValid = TryParseFinite(LengthText, out length);
        var thicknessValid = TryParseFinite(PlugThicknessText, out plugThickness);
        return !string.IsNullOrWhiteSpace(PlateNo)
               && serialValid
               && diameterValid && diameter > 0
               && lengthValid && length > 0
               && thicknessValid
               && (PlugHole == 70 || PlugHole == 100)
               && LatheMachiningMode >= WorkpieceInfo.LatheMachiningModeMin
               && LatheMachiningMode <= WorkpieceInfo.LatheMachiningModeMax;
    }

    // 不接收NaN/无穷大；不把逗号等分组符静默删掉，避免错误文本变成另一尺寸。
    private static bool TryParseFinite(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
}
