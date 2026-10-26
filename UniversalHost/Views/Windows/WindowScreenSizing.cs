using Avalonia;
using Avalonia.Controls;
using System;

namespace UniversalHost.Views.Windows;

internal static class WindowScreenSizing
{
    // 窗口的 Width/Height 使用 DIP，屏幕工作区和窗口位置使用物理像素。
    public static void FitAndCenter(Window window, Window? referenceWindow)
    {
        var screens = referenceWindow?.Screens ?? window.Screens;
        var screen = referenceWindow is null ? screens.Primary : screens.ScreenFromWindow(referenceWindow) ?? screens.Primary;
        if (screen is null || screen.Scaling <= 0) return;

        // 为系统窗口边框和标题栏预留空间。
        const double margin = 32;
        var workArea = screen.WorkingArea;
        var availableWidth = Math.Max(1, workArea.Width / screen.Scaling - margin * 2);
        var availableHeight = Math.Max(1, workArea.Height / screen.Scaling - margin * 2);

        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);
        window.Width = Math.Min(window.Width, availableWidth);
        window.Height = Math.Min(window.Height, availableHeight);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(
            workArea.X + (int)Math.Round((workArea.Width - window.Width * screen.Scaling) / 2),
            workArea.Y + (int)Math.Round((workArea.Height - window.Height * screen.Scaling) / 2));
    }
}
