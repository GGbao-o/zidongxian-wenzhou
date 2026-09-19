using System.Windows.Controls;

namespace AutomatedLine.Ui.Views;

/// <summary>
/// 大屏展示视图（只读，无控制按钮）。支持横竖滚动以容纳全部设备/天车。
/// </summary>
public partial class ScreenPageView : UserControl
{
    /// <summary>构造函数。</summary>
    public ScreenPageView()
    {
        InitializeComponent();
    }
}
