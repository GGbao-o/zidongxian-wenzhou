using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutomatedLine.Ui.ViewModels;

namespace AutomatedLine.Ui.Views;

/// <summary>
/// 报警只读列表，双击有效数据行打开统一详情。
/// </summary>
public partial class AlarmPageView : UserControl
{
    /// <summary>构造函数。</summary>
    public AlarmPageView()
    {
        InitializeComponent();
    }
    // 仅对数据行响应，双击表头、滚动条或空白处不打开旧选中报警。
    private void AlarmGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(grid, source) is DataGridRow { Item: AlarmRowViewModel row }
            && DataContext is AlarmViewModel viewModel)
        {
            viewModel.SelectedAlarm = row;
            viewModel.ViewDetailsCommand.Execute(null);
            e.Handled = true;
        }
    }
}
