using System.Windows;
using AutomatedLine.Ui.ViewModels;
using Wpf.Ui.Controls;

namespace AutomatedLine.Ui.Views;

/// <summary>
/// 参数分类编辑弹框。
/// 只负责收集编辑结果与确认/取消；写回与保存由 <see cref="ParamsViewModel"/> 负责。
/// </summary>
public partial class ParamsDialog : FluentWindow
{
    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="viewModel">参数编辑 ViewModel</param>
    public ParamsDialog(ParamsDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>确定：关闭并返回 true。</summary>
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>取消：关闭并返回 false。</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
