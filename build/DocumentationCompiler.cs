#nullable enable

using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UniversalHost.Build
{
    /// <summary>
    /// 仅由 MSBuild 执行，将 Markdown 解析为可直接编译的文档数据。
    /// 支持标题、段落、平级列表、表格和行内代码。
    /// </summary>
    public sealed class CompileDocumentation : Microsoft.Build.Utilities.Task
    {
        private static readonly Regex HeadingPattern = new Regex(@"^(#{1,6})\s+(.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex ListPattern = new Regex(@"^(?:([0-9]+)[.)]|[-+*])\s+(.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex TableSeparatorPattern = new Regex(@"^:?-{3,}:?$", RegexOptions.CultureInvariant);
        private static readonly Regex CodeSpanPattern = new Regex(@"(?<!`)(`+)(.+?)\1(?!`)", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        [Required]
        public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string OutputFile { get; set; } = string.Empty;

        public override bool Execute()
        {
            var documents = new List<SourceDocument>();
            var titles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in Sources)
            {
                string path = source.GetMetadata("FullPath");
                try
                {
                    string title = Path.GetFileNameWithoutExtension(path);
                    if (!titles.Add(title)) throw new FormatException("文档名称重复：" + title);
                    documents.Add(Parse(title, File.ReadAllText(path, Encoding.UTF8)));
                }
                catch (Exception error)
                {
                    var parseError = error as DocumentParseException;
                    Log.LogError(null, "UH_DOC001", null, path, parseError == null ? 0 : parseError.Line,
                        0, 0, 0, error.Message);
                    return false;
                }
            }

            try
            {
                string outputPath = Path.GetFullPath(OutputFile);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, GenerateSource(documents), new UTF8Encoding(false));
                Log.LogMessage(MessageImportance.Low, "已生成编译期文档：{0}", outputPath);
                return true;
            }
            catch (Exception error)
            {
                Log.LogErrorFromException(error);
                return false;
            }
        }

        private static SourceDocument Parse(string title, string markdown)
        {
            var document = new SourceDocument { Title = title };
            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int index = 0;
            while (index < lines.Length)
            {
                string line = lines[index].Trim();
                if (line.Length == 0)
                {
                    index++;
                    continue;
                }

                Match heading = HeadingPattern.Match(line);
                if (heading.Success)
                {
                    List<SourceInline> inlines = ParseInlines(heading.Groups[2].Value);
                    var plainTitle = new StringBuilder();
                    foreach (SourceInline inline in inlines) plainTitle.Append(inline.Text);
                    document.Headings.Add(new SourceHeading
                    {
                        BlockIndex = document.Blocks.Count,
                        Title = plainTitle.ToString(),
                        Level = heading.Groups[1].Length
                    });
                    document.Blocks.Add(new SourceBlock { Inlines = inlines, HeadingLevel = heading.Groups[1].Length });
                    index++;
                    continue;
                }

                Match item = ListPattern.Match(line);
                if (item.Success)
                {
                    document.Blocks.Add(new SourceBlock
                    {
                        Inlines = ParseInlines(item.Groups[2].Value),
                        ListMarker = item.Groups[1].Success ? item.Groups[1].Value + "." : "•"
                    });
                    index++;
                    continue;
                }

                if (IsTableStart(lines, index))
                {
                    List<string> header = SplitCells(line);
                    int columns = SplitCells(lines[index + 1].Trim()).Count;
                    ValidateColumns(header, columns, index);
                    var rows = new List<List<List<SourceInline>>> { ParseRow(header) };
                    index += 2;
                    while (index < lines.Length && lines[index].Contains("|") && !string.IsNullOrWhiteSpace(lines[index]))
                    {
                        List<string> row = SplitCells(lines[index].Trim());
                        ValidateColumns(row, columns, index);
                        rows.Add(ParseRow(row));
                        index++;
                    }
                    document.Blocks.Add(new SourceBlock { TableRows = rows });
                    continue;
                }

                var paragraph = new StringBuilder(line);
                index++;
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])
                    && !HeadingPattern.IsMatch(lines[index].Trim())
                    && !ListPattern.IsMatch(lines[index].Trim()) && !IsTableStart(lines, index))
                {
                    paragraph.Append(' ').Append(lines[index].Trim());
                    index++;
                }
                document.Blocks.Add(new SourceBlock { Inlines = ParseInlines(paragraph.ToString()) });
            }
            return document;
        }

        private static bool IsTableStart(string[] lines, int index)
        {
            if (index + 1 >= lines.Length || !lines[index].Contains("|")) return false;
            List<string> separators = SplitCells(lines[index + 1].Trim());
            if (separators.Count == 0) return false;
            foreach (string cell in separators)
                if (!TableSeparatorPattern.IsMatch(cell)) return false;
            return true;
        }

        private static void ValidateColumns(List<string> row, int columns, int index)
        {
            if (row.Count != columns) throw new DocumentParseException("表格列数不一致。", index + 1);
        }

        private static List<List<SourceInline>> ParseRow(List<string> cells)
        {
            var row = new List<List<SourceInline>>();
            foreach (string cell in cells) row.Add(ParseInlines(cell));
            return row;
        }

        private static List<string> SplitCells(string line)
        {
            var cells = new List<string>();
            var cell = new StringBuilder();
            int codeDelimiter = 0;
            bool trailingSeparator = false;
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '\\' && index + 1 < line.Length && line[index + 1] == '|')
                {
                    cell.Append('|');
                    index++;
                }
                else if (character == '`')
                {
                    int start = index;
                    while (index + 1 < line.Length && line[index + 1] == '`') index++;
                    int length = index - start + 1;
                    if (codeDelimiter == 0) codeDelimiter = length;
                    else if (codeDelimiter == length) codeDelimiter = 0;
                    cell.Append('`', length);
                }
                else if (character == '|' && codeDelimiter == 0)
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                    trailingSeparator = index == line.Length - 1;
                }
                else cell.Append(character);
            }
            cells.Add(cell.ToString().Trim());
            if (line.StartsWith("|", StringComparison.Ordinal)) cells.RemoveAt(0);
            if (trailingSeparator) cells.RemoveAt(cells.Count - 1);
            return cells;
        }

        private static List<SourceInline> ParseInlines(string text)
        {
            var inlines = new List<SourceInline>();
            int position = 0;
            foreach (Match match in CodeSpanPattern.Matches(text))
            {
                if (match.Index > position)
                    inlines.Add(new SourceInline { Text = text.Substring(position, match.Index - position) });
                string code = match.Groups[2].Value.Replace('\n', ' ');
                if (code.StartsWith(" ", StringComparison.Ordinal) && code.EndsWith(" ", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(code)) code = code.Substring(1, code.Length - 2);
                inlines.Add(new SourceInline { Text = code, IsCode = true });
                position = match.Index + match.Length;
            }
            if (position < text.Length) inlines.Add(new SourceInline { Text = text.Substring(position) });
            return inlines;
        }

        private static string GenerateSource(List<SourceDocument> documents)
        {
            var code = new StringBuilder();
            code.AppendLine("// <auto-generated />");
            code.AppendLine("using System;");
            code.AppendLine("using System.IO;");
            code.AppendLine("using UniversalHost.Models;");
            code.AppendLine("namespace UniversalHost.Services;");
            code.AppendLine("internal static class CompiledDocumentation");
            code.AppendLine("{");
            code.AppendLine("    public static Documentation Get(string title) => title switch");
            code.AppendLine("    {");
            for (int index = 0; index < documents.Count; index++)
                code.Append("        ").Append(Quote(documents[index].Title)).Append(" => Document").Append(index).AppendLine(".Value,");
            code.AppendLine("        _ => throw new FileNotFoundException(\"找不到编译期文档：\" + title)");
            code.AppendLine("    };");

            for (int index = 0; index < documents.Count; index++)
            {
                SourceDocument document = documents[index];
                code.Append("    private static readonly Lazy<Documentation> Document").Append(index)
                    .Append(" = new(CreateDocument").Append(index).AppendLine(");");
                code.Append("    private static Documentation CreateDocument").Append(index).AppendLine("() => new(");
                code.Append("        ").Append(Quote(document.Title)).AppendLine(",");
                code.AppendLine("        new DocumentationBlock[]");
                code.AppendLine("        {");
                foreach (SourceBlock block in document.Blocks)
                {
                    code.Append("            new(");
                    AppendInlines(code, block.Inlines);
                    if (block.HeadingLevel > 0) code.Append(", HeadingLevel: ").Append(block.HeadingLevel);
                    if (block.ListMarker != null) code.Append(", ListMarker: ").Append(Quote(block.ListMarker));
                    if (block.TableRows != null)
                    {
                        code.AppendLine(", TableRows: new DocumentationCell[][]");
                        code.AppendLine("            {");
                        foreach (List<List<SourceInline>> row in block.TableRows)
                        {
                            code.Append("                new DocumentationCell[] { ");
                            foreach (List<SourceInline> cell in row)
                            {
                                code.Append("new(");
                                AppendInlines(code, cell);
                                code.Append("), ");
                            }
                            code.AppendLine("},");
                        }
                        code.Append("            }");
                    }
                    code.AppendLine("),");
                }
                code.AppendLine("        },");
                code.AppendLine("        new DocumentationHeading[]");
                code.AppendLine("        {");
                foreach (SourceHeading heading in document.Headings)
                    code.Append("            new(").Append(heading.BlockIndex).Append(", ")
                        .Append(Quote(heading.Title)).Append(", ").Append(heading.Level).AppendLine("),");
                code.AppendLine("        });");
            }
            code.AppendLine("}");
            return code.ToString();
        }

        private static void AppendInlines(StringBuilder code, List<SourceInline> inlines)
        {
            code.Append("new DocumentationInline[] { ");
            foreach (SourceInline inline in inlines)
            {
                code.Append("new(").Append(Quote(inline.Text));
                if (inline.IsCode) code.Append(", IsCode: true");
                code.Append("), ");
            }
            code.Append("}");
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\': result.Append(@"\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\n': result.Append(@"\n"); break;
                    case '\r': result.Append(@"\r"); break;
                    case '\t': result.Append(@"\t"); break;
                    default:
                        if (char.IsControl(character) || character == '\u2028' || character == '\u2029')
                            result.Append(@"\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                        else result.Append(character);
                        break;
                }
            }
            return result.Append('"').ToString();
        }

        private sealed class SourceDocument
        {
            public string Title = string.Empty;
            public readonly List<SourceBlock> Blocks = new List<SourceBlock>();
            public readonly List<SourceHeading> Headings = new List<SourceHeading>();
        }

        private sealed class SourceBlock
        {
            public List<SourceInline> Inlines = new List<SourceInline>();
            public int HeadingLevel;
            public string? ListMarker;
            public List<List<List<SourceInline>>>? TableRows;
        }

        private sealed class SourceInline
        {
            public string Text = string.Empty;
            public bool IsCode;
        }

        private sealed class SourceHeading
        {
            public int BlockIndex;
            public string Title = string.Empty;
            public int Level;
        }

        private sealed class DocumentParseException : FormatException
        {
            public int Line { get; private set; }
            public DocumentParseException(string message, int line) : base(message) { Line = line; }
        }
    }
}
