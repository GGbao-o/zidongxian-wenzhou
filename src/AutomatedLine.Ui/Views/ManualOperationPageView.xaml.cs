using System.Windows.Controls;

namespace AutomatedLine.Ui.Views;

/// <summary>
/// 手动操作视图。选择天车并执行三轴绝对/相对运动、设速度、清报警、充放磁/下压等动作。
/// </summary>
public partial class ManualOperationPageView : UserControl
{
    /// <summary>构造函数。</summary>
    public ManualOperationPageView()
    {
        InitializeComponent();
    }
}
