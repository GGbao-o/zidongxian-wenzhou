using AutomatedLine.Domain.Workpieces;

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
}
