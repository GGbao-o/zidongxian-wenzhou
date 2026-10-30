using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace AutomatedLine.Ui;

/// <summary>独立于容器和数据库的轻量启动提示，持续显示阶段与已耗时。</summary>
public partial class StartupWindow : Window
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>创建提示并启动UI计时；关闭后停止计时，避免窗口残留引用。</summary>
    public StartupWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => ElapsedText.Text = $"已用时 {_watch.Elapsed.TotalSeconds:0.0} 秒，请稍候";
        Closed += (_, _) => _timer.Stop();
        _timer.Start();
    }

    /// <summary>更新当前阶段，须在UI线程调用。</summary>
    /// <param name="stage">面向操作员的阶段说明。</param>
    public void SetStage(string stage) => StageText.Text = stage;
}
