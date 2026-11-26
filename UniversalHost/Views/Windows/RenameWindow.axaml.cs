using Avalonia.Controls;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System;
using UniversalHost.ViewModels.Windows;

namespace UniversalHost.Views.Windows;

public partial class RenameWindow : ReactiveWindow<RenameWindowViewModel>
{
    public RenameWindow()
    {
        InitializeComponent();

        Opened += (s, e) =>
        {
            var textBox = this.FindControl<TextBox>("NameInput");
            if (textBox != null)
            {
                textBox.Focus();
                // 如果有初始文本，全选它，方便用户直接按键盘覆盖
                if (!string.IsNullOrEmpty(textBox.Text))
                {
                    textBox.SelectionStart = 0;
                    textBox.SelectionEnd = textBox.Text.Length;
                }
            }
        };

        this.WhenActivated(disposables =>
        {
            if (ViewModel == null) return;

            // 订阅确认命令，关闭窗口并传递新名称
            disposables.Add(ViewModel.ConfirmCommand.Subscribe(result => Close(result)));

            // 订阅取消命令，关闭窗口并传递 null
            disposables.Add(ViewModel.CancelCommand.Subscribe(result => Close(result)));
        });
    }
}
