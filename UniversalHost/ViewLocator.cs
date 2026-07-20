using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Core;
using StaticViewLocator;

namespace UniversalHost;

[StaticViewLocator]
public partial class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is IDockable dockable)
        {
            var context = dockable.Context;

            if (context == null)
                return new TextBlock { Text = $"No context: {dockable.Id}" };

            var type = context.GetType();

            if (s_views.TryGetValue(type, out var factory))
            {
                var view = factory();
                view.DataContext = context;
                return view;
            }

            return new TextBlock { Text = $"No view for context: {type}" };
        }

        return new TextBlock { Text = $"Unknown type: {data?.GetType()}" };
    }

    public bool Match(object? data)
    {
        if (data is null)
        {
            return false;
        }

        var type = data.GetType();
        return data is IDockable || s_views.ContainsKey(type);
    }
}
