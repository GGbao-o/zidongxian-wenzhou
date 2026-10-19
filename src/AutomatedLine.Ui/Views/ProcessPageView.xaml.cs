using System.Windows.Controls;

namespace AutomatedLine.Ui.Views;

/// <summary>
/// 工序设置视图；加工工序勾选修改后由ViewModel保存并弹出最新完整路线。
/// </summary>
public partial class ProcessPageView : UserControl
{
    /// <summary>构造函数。</summary>
    public ProcessPageView()
    {
        InitializeComponent();
    }
}
