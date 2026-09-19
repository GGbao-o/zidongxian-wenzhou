using Wpf.Ui.Controls;

namespace AutomatedLine.Ui;

/// <summary>
/// 主窗口。
/// 视图层只负责展示与转发操作，业务逻辑全部在 <c>MainViewModel</c> 中；
/// DataContext 由 App.xaml.cs 从 DI 容器解析后注入，窗口自身不构造 ViewModel。
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>
    /// 构造函数。仅初始化 XAML 组件，不在这里做业务装配。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
