using System.Windows;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Ui.ViewModels;
using Wpf.Ui.Controls;

namespace AutomatedLine.Ui.Views;

/// <summary>
/// 添加工件弹窗。
/// 视图只负责收集输入与确认/取消交互，校验规则与数据组装在 <see cref="AddWorkpieceViewModel"/>。
/// </summary>
public partial class AddWorkpieceDialog : FluentWindow
{
    /// <summary>弹窗的 ViewModel。</summary>
    public AddWorkpieceViewModel ViewModel { get; }

    /// <summary>
    /// 用户确认后组装出的工件数据；取消时为 null。
    /// </summary>
    public WorkpieceInfo? Result { get; private set; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="viewModel">输入 ViewModel</param>
    public AddWorkpieceDialog(AddWorkpieceViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// 确定按钮：校验通过后组装数据并关闭窗口。
    /// </summary>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Validate())
        {
            // 校验失败给出提示，不关闭窗口，避免用户重新输入。
            // 全限定名：Wpf.Ui.Controls 也有 MessageBox，直接用 MessageBox 会产生歧义
            System.Windows.MessageBox.Show(this, "请检查输入：版号必填；序号为非负整数（>=0）；直径、长度为有限正数；堵厚为有限数值；堵孔只能填 70 或 100；斜床工艺只能填 1~10。", "输入不合法",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        Result = ViewModel.BuildWorkpieceInfo();
        DialogResult = true;
    }

    /// <summary>
    /// 取消按钮：直接关闭，不产生数据。
    /// </summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
