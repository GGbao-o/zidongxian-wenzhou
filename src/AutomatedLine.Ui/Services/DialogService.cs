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
    public bool ShowParamsDialog(ParamsDialogViewModel viewModel)
    {
        var dialog = new ParamsDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true;
    }
}
