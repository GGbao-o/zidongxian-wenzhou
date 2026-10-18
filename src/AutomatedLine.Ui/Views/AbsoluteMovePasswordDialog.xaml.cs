using System.Windows;
using Wpf.Ui.Controls;

namespace AutomatedLine.Ui.Views;

/// <summary>手动绝对移动的单次密码确认框；输入错误保留弹窗，取消不放行动作。</summary>
public partial class AbsoluteMovePasswordDialog : FluentWindow
{
    // 仅在本次弹窗内用于比对，不回显到界面或写入日志。
    private readonly string _expectedPassword;

    /// <summary>创建本次绝对移动的确认框。</summary>
    /// <param name="message">待执行的天车和目标坐标说明。</param>
    /// <param name="expectedPassword">配置中的确认密码；空白时不能确认通过。</param>
    public AbsoluteMovePasswordDialog(string message, string expectedPassword)
    {
        _expectedPassword = expectedPassword;
        InitializeComponent();
        PromptText.Text = message;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>密码完全匹配才返回确认；输错不关闭弹窗、不执行任何运动。</summary>
    /// <param name="sender">确认按钮。</param>
    /// <param name="e">点击事件。</param>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_expectedPassword)
            || !string.Equals(PasswordInput.Password, _expectedPassword, StringComparison.Ordinal))
        {
            ErrorText.Text = "密码错误，请重新输入。";
            PasswordInput.Clear();
            PasswordInput.Focus();
            return;
        }

        PasswordInput.Clear();
        DialogResult = true;
    }

    /// <summary>取消本次确认，调用方不下发绝对移动指令。</summary>
    /// <param name="sender">取消按钮。</param>
    /// <param name="e">点击事件。</param>
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
