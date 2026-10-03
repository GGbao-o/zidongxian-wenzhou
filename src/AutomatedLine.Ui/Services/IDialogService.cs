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
    /// 弹出"编辑工件"对话框：用现有数据预填，进入编辑模式
    /// （版号 / 序号 / 是否做动平衡只读，其余字段可改）。
    /// </summary>
    /// <param name="current">当前工件数据（用于预填；<c>RuntimeId</c> 会被沿用）</param>
    /// <returns>用户确定返回新的工件数据；取消返回 null</returns>
    WorkpieceInfo? ShowEditWorkpieceDialog(WorkpieceInfo current);

    /// <summary>
    /// 弹出一条提示信息（ViewModel 不直接引用 MessageBox）。
    /// </summary>
    /// <param name="message">提示内容</param>
    /// <param name="title">标题</param>
    void ShowMessage(string message, string title);

    /// <summary>
    /// 弹出参数分类编辑对话框。
    /// </summary>
    /// <param name="viewModel">参数编辑 ViewModel</param>
    /// <returns>用户确定返回 true；取消返回 false</returns>
    bool ShowParamsDialog(ParamsDialogViewModel viewModel);
}
