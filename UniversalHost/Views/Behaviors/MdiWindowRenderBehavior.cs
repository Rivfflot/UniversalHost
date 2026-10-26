using Avalonia;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace UniversalHost.Views.Behaviors;

public sealed class MdiWindowRenderBehavior : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<MdiWindowRenderBehavior, MdiDocumentWindow, bool>("IsEnabled");

    static MdiWindowRenderBehavior()
    {
        Visual.ZIndexProperty.Changed.AddClassHandler<MdiDocumentWindow>(OnZIndexChanged);
    }

    public static bool GetIsEnabled(MdiDocumentWindow window) => window.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(MdiDocumentWindow window, bool value) => window.SetValue(IsEnabledProperty, value);

    private static void OnZIndexChanged(MdiDocumentWindow window, AvaloniaPropertyChangedEventArgs change)
    {
        if (!GetIsEnabled(window)) return;

        // Avalonia 12.1.4 更新合成层级后可能保留旧的遮挡区域，只重绘发生变化的标题栏。
        // MdiDocumentWindow 自身没有绘图命令，仅 InvalidateVisual 不足以刷新其内容；
        // 在层级变化时一并使子树失效，让静态控件与图表也在同一帧完整显示。
        window.InvalidateVisual();
        foreach (var visual in window.GetVisualDescendants())
            visual.InvalidateVisual();
    }
}
