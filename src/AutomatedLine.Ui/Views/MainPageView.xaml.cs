using System.Windows.Controls;

namespace AutomatedLine.Ui.Views;

/// <summary>
/// 主页面视图。承载概览卡片与工件任务列表；DataContext 由外壳通过 DataTemplate 注入。
/// </summary>
public partial class MainPageView : UserControl
{
    /// <summary>构造函数。</summary>
    public MainPageView()
    {
        InitializeComponent();
    }
}
