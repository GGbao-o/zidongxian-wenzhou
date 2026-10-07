using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Ui.ViewModels;
using AutomatedLine.Ui.Views;

namespace AutomatedLine.Ui.Services;

/// <summary>
/// 弹窗服务实现。
/// 负责创建对话框与其 ViewModel，并把主窗口设为 Owner，保证弹窗居中且模态正确。
/// </summary>
public class DialogService : IDialogService
{
    /// <inheritdoc />
    public WorkpieceInfo? ShowAddWorkpieceDialog()
    {
        // 弹窗 ViewModel 无外部依赖，直接构造即可；若后续依赖增多再改为 DI 解析
        var viewModel = new AddWorkpieceViewModel();
        var dialog = new AddWorkpieceDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        // ShowDialog 为模态，返回 true 表示用户点击了确定
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <inheritdoc />
    public WorkpieceInfo? ShowEditWorkpieceDialog(WorkpieceInfo current)
    {
        var viewModel = new AddWorkpieceViewModel();
        // 预填现有数据并进入编辑模式：版号/序号在编辑模式下只读
        viewModel.LoadFrom(current);

        var dialog = new AddWorkpieceDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <inheritdoc />
    public void ShowMessage(string message, string title)
    {
        // 全限定名：Wpf.Ui.Controls 也有 MessageBox，直接用 MessageBox 会产生歧义
        System.Windows.MessageBox.Show(
            System.Windows.Application.Current?.MainWindow ?? System.Windows.Application.Current!.MainWindow,
            message,
            title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    /// <inheritdoc />
    public bool ShowParamsDialog(ParamsDialogViewModel viewModel)
    {
        var dialog = new ParamsDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true;
    }
}
