using System.ComponentModel;
using System.Windows;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

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

    /// <summary>关闭窗口前确认，避免误触关闭按钮而停止后台服务。</summary>
    /// <param name="e">关闭事件参数；用户取消时阻止本次关闭。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || System.Windows.Application.Current is App { IsShutdownComplete: true }) return;

        // 在窗口真正关闭、触发宿主退出前询问；默认选择取消，避免误按回车退出。
        var result = System.Windows.MessageBox.Show(this,
            "确定要关闭程序吗？", "关闭程序",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        e.Cancel = result != MessageBoxResult.OK;
        if (!e.Cancel && System.Windows.Application.Current is App app)
        {
            // WPF的OnExit不会等待async void；先阻止关闭，宿主停止后由App真正退出。
            e.Cancel = true;
            _ = app.ShutdownApplicationAsync();
        }
    }
}
