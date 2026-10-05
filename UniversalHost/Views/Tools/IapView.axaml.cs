using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UniversalHost.Views.Tools;

public partial class IapView : UserControl
{
    public IapView()
    {
        InitializeComponent();
    }

    private void ShowIapFilePathEnd(object? sender, RoutedEventArgs e)
    {
        // 加载或更新路径后显示文件名，手动编辑时保留当前光标位置。
        if (sender is TextBox textBox && !textBox.IsFocused)
        {
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
        }
    }
}
