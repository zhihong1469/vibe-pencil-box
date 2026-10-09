// <copyright file="PromptDialog.xaml.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Windows;

namespace PencilBox.Desktop.Views;

/// <summary>
/// 单行文本输入小对话框（Keymap 配置另存为/改名用；WPF 无内置 InputBox）。
/// </summary>
public partial class PromptDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public PromptDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        ValueBox.SelectAll();
        ValueBox.Focus();
    }

    /// <summary>模态弹出；确定返回输入文本（去首尾空白），取消返回 null。</summary>
    public static string? Show(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = new PromptDialog(title, prompt, initial) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            MessageBox.Show(this, "内容不能为空", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
