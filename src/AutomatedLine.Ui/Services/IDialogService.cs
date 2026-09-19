using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Ui.ViewModels;

namespace AutomatedLine.Ui.Services;

/// <summary>
/// 弹窗服务抽象。
/// 让 ViewModel 能"请求弹出对话框"而不直接引用具体 View，保持可测试性与低耦合。
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// 弹出"添加工件"对话框。
    /// </summary>
    /// <returns>用户确认返回工件数据；取消返回 null</returns>
    WorkpieceInfo? ShowAddWorkpieceDialog();

    /// <summary>
    /// 弹出参数分类编辑对话框。
    /// </summary>
    /// <param name="viewModel">参数编辑 ViewModel</param>
    /// <returns>用户确定返回 true；取消返回 false</returns>
    bool ShowParamsDialog(ParamsDialogViewModel viewModel);
}
