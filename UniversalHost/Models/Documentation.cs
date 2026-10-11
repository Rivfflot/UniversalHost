using System.Collections.Generic;

namespace UniversalHost.Models;

public sealed record Documentation(
    string Title,
    IReadOnlyList<DocumentationBlock> Blocks,
    IReadOnlyList<DocumentationHeading> Headings);

public sealed record DocumentationBlock(
    IReadOnlyList<DocumentationInline> Inlines,
    int HeadingLevel = 0,
    string? ListMarker = null,
    IReadOnlyList<IReadOnlyList<DocumentationCell>>? TableRows = null);

public sealed record DocumentationInline(string Text, bool IsCode = false);

public sealed record DocumentationCell(IReadOnlyList<DocumentationInline> Inlines);

public sealed record DocumentationHeading(int BlockIndex, string Title, int Level);
