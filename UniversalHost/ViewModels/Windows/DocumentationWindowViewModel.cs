using Avalonia;
using System.Collections.Generic;
using System.Linq;
using UniversalHost.Models;

namespace UniversalHost.ViewModels.Windows;

public sealed class DocumentationWindowViewModel
{
    public Documentation Document { get; }
    public string Title => Document.Title;
    public IReadOnlyList<DocumentationHeadingViewModel> Headings { get; }

    public DocumentationWindowViewModel(Documentation document)
    {
        Document = document;
        Headings = document.Headings
            .Select(heading => new DocumentationHeadingViewModel(heading.BlockIndex, heading.Title, heading.Level))
            .ToArray();
    }
}

public sealed record DocumentationHeadingViewModel(int BlockIndex, string Title, int Level)
{
    public Thickness Indent => new((Level - 1) * 14, 0, 0, 0);
}
