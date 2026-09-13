using System.Windows;

namespace MaggotTask.View;

/// <summary>
/// 二次确认对话框（危险动作专用）。
/// 设计系统 §7：<c>清空</c> 必须二次确认，且确认文案给出具体数量 ——「将永久删除 N 个任务」。
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>危险动作确认。返回 true 表示用户确认。</summary>
    public static bool Ask(Window? owner, string title, string body, string confirmText = "确认")
    {
        ConfirmDialog dlg = new()
        {
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        dlg.TitleText.Text = title;
        dlg.BodyText.Text = body;
        dlg.OkButton.Content = confirmText;
        return dlg.ShowDialog() == true;
    }

    /// <summary>纯信息提示。</summary>
    public static void Info(Window? owner, string title, string body)
    {
        ConfirmDialog dlg = new()
        {
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        dlg.TitleText.Text = title;
        dlg.BodyText.Text = body;
        dlg.OkButton.Content = "知道了";
        dlg.OkButton.Style = (Style)dlg.FindResource("PrimaryOutlinedButton");
        dlg.CancelButton.Visibility = Visibility.Collapsed;
        dlg.ShowDialog();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
