"""Render user documentation Markdown with Python's standard library and Edge/Chrome."""

import argparse
import html
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time


def inline(text: str) -> str:
    # A code span can contain backticks when delimited by a longer backtick run.
    pattern = r"(`+)(.+?)\1(?!`)"
    result = []
    position = 0
    for match in re.finditer(pattern, text):
        result.append(html.escape(text[position:match.start()]))
        code = match.group(2).replace("\n", " ")
        if code.startswith(" ") and code.endswith(" ") and code.strip():
            code = code[1:-1]
        result.append(f"<code>{html.escape(code)}</code>")
        position = match.end()
    result.append(html.escape(text[position:]))
    return "".join(result)


def cells(line: str) -> list[str]:
    # Split outside code spans, preserving pipes in key names and escaped pipes.
    parts = []
    start = 0
    code_delimiter = 0
    index = 0
    while index < len(line):
        if line[index] == "\\" and index + 1 < len(line) and line[index + 1] == "|":
            index += 2
            continue
        if line[index] == "`":
            end = index + 1
            while end < len(line) and line[end] == "`":
                end += 1
            length = end - index
            if not code_delimiter:
                code_delimiter = length
            elif code_delimiter == length:
                code_delimiter = 0
            index = end
            continue
        if line[index] == "|" and not code_delimiter:
            parts.append(line[start:index].strip().replace(r"\|", "|"))
            start = index + 1
        index += 1
    parts.append(line[start:].strip().replace(r"\|", "|"))
    if line.startswith("|"):
        parts.pop(0)
    if line.endswith("|"):
        parts.pop()
    return parts


def markdown_body(markdown: str) -> str:
    """Support headings, paragraphs, flat lists, tables and code spans."""
    lines = markdown.splitlines()
    blocks = []
    headings = []
    toc_position = 0
    index = 0
    while index < len(lines):
        line = lines[index].strip()
        if not line:
            index += 1
            continue
        heading = re.fullmatch(r"(#{1,6})\s+(.+)", line)
        if heading:
            level = len(heading.group(1))
            anchor = f"section-{len(headings) + 1}"
            title = inline(heading.group(2))
            headings.append((level, anchor, title))
            if len(headings) == 1 and level == 1:
                # The visible document title must not wrap the PDF bookmarks.
                blocks.append(f'<div class="document-title" id="{anchor}">{title}</div>')
                toc_position = len(blocks)
            else:
                blocks.append(f'<h{level} id="{anchor}">{title}</h{level}>')
            index += 1
            continue
        list_item = re.fullmatch(r"(?:(\d+)[.)]|([-+*]))\s+(.+)", line)
        if list_item:
            ordered = list_item.group(1) is not None
            tag = "ol" if ordered else "ul"
            start = f' start="{int(list_item.group(1))}"' if ordered else ""
            items = [f"<{tag}{start}>"]
            while index < len(lines):
                item = re.fullmatch(r"(?:(\d+)[.)]|([-+*]))\s+(.+)", lines[index].strip())
                if not item or (item.group(1) is not None) != ordered:
                    break
                items.append(f"<li>{inline(item.group(3))}</li>")
                index += 1
            items.append(f"</{tag}>")
            blocks.append("\n".join(items))
            continue
        if "|" in line and index + 1 < len(lines):
            separators = cells(lines[index + 1].strip())
            if separators and all(re.fullmatch(r":?-{3,}:?", cell) for cell in separators):
                header = cells(line)
                if len(header) != len(separators):
                    raise ValueError(f"Table column count mismatch at line {index + 1}")
                rows = ["<table><thead><tr>" + "".join(
                    f"<th>{inline(cell)}</th>" for cell in header
                ) + "</tr></thead><tbody>"]
                index += 2
                while index < len(lines) and "|" in lines[index] and lines[index].strip():
                    row = cells(lines[index].strip())
                    if len(row) != len(header):
                        raise ValueError(f"Table column count mismatch at line {index + 1}")
                    rows.append("<tr>" + "".join(f"<td>{inline(cell)}</td>" for cell in row) + "</tr>")
                    index += 1
                rows.append("</tbody></table>")
                blocks.append("\n".join(rows))
                continue
        paragraph = [line]
        index += 1
        while index < len(lines) and lines[index].strip():
            if lines[index].lstrip().startswith(("#", "|")):
                break
            if re.fullmatch(r"(?:(\d+)[.)]|([-+*]))\s+(.+)", lines[index].strip()):
                break
            paragraph.append(lines[index].strip())
            index += 1
        blocks.append(f"<p>{inline(' '.join(paragraph))}</p>")
    entries = [
        f'<li class="toc-level-{level}"><a href="#{anchor}">{title}</a></li>'
        for level, anchor, title in headings if level > 1
    ]
    if entries:
        blocks.insert(toc_position, '<nav class="toc" aria-label="目录">'
                      '<h2>目录</h2><ul>' + "\n".join(entries) + '</ul></nav>')
    return "\n".join(blocks)


def find_browser(explicit: str | None) -> Path:
    if explicit:
        candidate = Path(explicit)
        if candidate.is_file():
            return candidate.resolve()
        raise FileNotFoundError(f"Browser not found: {explicit}")
    for variable in ("ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA"):
        root = os.environ.get(variable)
        if root:
            for relative in ("Microsoft/Edge/Application/msedge.exe", "Google/Chrome/Application/chrome.exe"):
                candidate = Path(root) / relative
                if candidate.is_file():
                    return candidate
    for name in ("msedge", "microsoft-edge", "google-chrome", "chromium", "chromium-browser"):
        executable = shutil.which(name)
        if executable:
            return Path(executable)
    raise FileNotFoundError("Edge/Chrome not found. Specify --browser or MSBuild property ShortcutsPdfBrowser.")


STYLE = """
@page { size: A4; margin: 15mm;
        @bottom-center { content: counter(page); font-size: 9pt; color: #66758a; } }
body { font-family: 'Microsoft YaHei', 'Noto Sans CJK SC', sans-serif;
       font-size: 10pt; line-height: 1.55; color: #202b38; }
h1, .document-title { font-size: 22pt; font-weight: bold; margin: 0 0 12pt; }
h2 { font-size: 14pt; margin: 16pt 0 6pt; border-bottom: 1pt solid #b7c4d3; }
h3 { font-size: 11pt; margin: 12pt 0 5pt; }
h1, h2, h3, h4, h5, h6, .document-title { break-after: avoid; }
p { margin: 5pt 0 8pt; }
ol, ul { margin: 5pt 0 8pt; padding-left: 20pt; }
li { margin: 0 0 5pt; break-inside: avoid; }
table { width: 100%; border-collapse: collapse; margin: 0 0 10pt; font-size: 9pt; }
thead { display: table-header-group; }
tr { break-inside: avoid; }
th, td { border: 0.5pt solid #c6ced8; padding: 5pt 7pt;
         text-align: left; vertical-align: top; overflow-wrap: anywhere; }
th { background: #eaf0f7; font-weight: 600; }
code { font-family: Consolas, 'Microsoft YaHei', 'Noto Sans CJK SC', monospace; }
.toc { break-after: page; }
.toc h2 { margin: 8pt 0 10pt; }
.toc ul { list-style: none; margin: 0; padding: 0; }
.toc li { margin: 0; line-height: 1.35; }
.toc a { display: block; padding: 2pt 0; color: #202b38; text-decoration: none; }
.toc .toc-level-3 { margin-left: 14pt; }
.toc .toc-level-4 { margin-left: 28pt; }
.toc .toc-level-5 { margin-left: 42pt; }
.toc .toc-level-6 { margin-left: 56pt; }
"""


def convert(source: Path, destination: Path, browser: Path) -> None:
    body = markdown_body(source.read_text(encoding="utf-8-sig"))
    document = (f'<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">'
                f"<title>{html.escape(source.stem)}</title><style>{STYLE}</style>"
                f"</head><body>{body}</body></html>")
    destination.parent.mkdir(parents=True, exist_ok=True)
    # Keep Chromium's profile isolated from the user's running browser. A fresh
    # temporary PDF also ensures that an old publish file cannot mask failure.
    with tempfile.TemporaryDirectory(prefix="universalhost-pdf-", ignore_cleanup_errors=True) as directory:
        temporary = Path(directory)
        input_html = temporary / "document.html"
        output_pdf = temporary / "document.pdf"
        input_html.write_text(document, encoding="utf-8")
        deadline = time.monotonic() + 90
        result = subprocess.run([
            str(browser), "--headless", "--disable-gpu", "--no-first-run",
            "--no-default-browser-check", "--disable-extensions",
            f"--user-data-dir={temporary / 'profile'}",
            "--no-pdf-header-footer", "--print-to-pdf-no-header",
            "--export-tagged-pdf", "--generate-pdf-document-outline",
            f"--print-to-pdf={output_pdf}", input_html.as_uri(),
        ], capture_output=True, timeout=90)
        # On Windows the browser launcher can exit before its worker writes the
        # PDF. Keep the HTML/profile alive and wait for a complete file, within
        # the same overall timeout, rather than accepting a partly written PDF.
        while result.returncode == 0 and time.monotonic() < deadline:
            try:
                with output_pdf.open("rb") as stream:
                    header = stream.read(5)
                    stream.seek(0, os.SEEK_END)
                    stream.seek(max(0, stream.tell() - 32))
                    if header == b"%PDF-" and stream.read().rstrip().endswith(b"%%EOF"):
                        break
            except OSError:
                pass
            time.sleep(0.1)
        else:
            details = result.stderr.decode("utf-8", errors="replace")[-3000:]
            raise RuntimeError(f"Browser PDF conversion failed (exit {result.returncode}): {details}")
        shutil.copyfile(output_pdf, destination)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--browser", help="Path to Edge/Chrome executable")
    arguments = parser.parse_args()
    try:
        browser = find_browser(arguments.browser)
    except FileNotFoundError as error:
        print(f"warning SHORTCUTSPDF001: {error} Skipping PDF generation for {arguments.source.name}; publish will continue.")
        return 0
    try:
        convert(arguments.source.resolve(), arguments.destination.resolve(), browser)
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"Documentation PDF ({arguments.source.name}): {error}", file=sys.stderr)
        return 1
    print(f"Documentation PDF generated: {arguments.destination.resolve()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
