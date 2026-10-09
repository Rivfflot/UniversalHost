"""Render the shortcuts Markdown with Python's standard library and Edge/Chrome."""

import argparse
import html
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


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
    """Support the headings, paragraphs, tables and code spans used by this doc."""
    lines = markdown.splitlines()
    blocks = []
    index = 0
    while index < len(lines):
        line = lines[index].strip()
        if not line:
            index += 1
            continue
        heading = re.fullmatch(r"(#{1,6})\s+(.+)", line)
        if heading:
            level = len(heading.group(1))
            blocks.append(f"<h{level}>{inline(heading.group(2))}</h{level}>")
            index += 1
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
            paragraph.append(lines[index].strip())
            index += 1
        blocks.append(f"<p>{inline(' '.join(paragraph))}</p>")
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
@page { size: A4; margin: 15mm; }
body { font-family: 'Microsoft YaHei', 'Noto Sans CJK SC', sans-serif;
       font-size: 10pt; line-height: 1.55; color: #202b38; }
h1 { font-size: 22pt; margin: 0 0 12pt; }
h2 { font-size: 14pt; margin: 16pt 0 6pt; border-bottom: 1pt solid #b7c4d3; }
h3 { font-size: 11pt; margin: 12pt 0 5pt; }
h1, h2, h3, h4, h5, h6 { break-after: avoid; }
p { margin: 5pt 0 8pt; }
table { width: 100%; border-collapse: collapse; margin: 0 0 10pt; font-size: 9pt; }
thead { display: table-header-group; }
tr { break-inside: avoid; }
th, td { border: 0.5pt solid #c6ced8; padding: 5pt 7pt;
         text-align: left; vertical-align: top; overflow-wrap: anywhere; }
th { background: #eaf0f7; font-weight: 600; }
code { font-family: Consolas, 'Microsoft YaHei', 'Noto Sans CJK SC', monospace; }
"""


def convert(source: Path, destination: Path, browser: Path) -> None:
    body = markdown_body(source.read_text(encoding="utf-8-sig"))
    document = (f'<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">'
                f"<title>{html.escape(source.stem)}</title><style>{STYLE}</style>"
                f"</head><body>{body}</body></html>")
    destination.parent.mkdir(parents=True, exist_ok=True)
    # Keep Chromium's profile isolated from the user's running browser. A fresh
    # temporary PDF also ensures that an old publish file cannot mask failure.
    with tempfile.TemporaryDirectory(prefix="universalhost-pdf-") as directory:
        temporary = Path(directory)
        input_html = temporary / "shortcuts.html"
        output_pdf = temporary / "shortcuts.pdf"
        input_html.write_text(document, encoding="utf-8")
        result = subprocess.run([
            str(browser), "--headless", "--disable-gpu", "--no-first-run",
            "--no-default-browser-check", "--disable-extensions",
            f"--user-data-dir={temporary / 'profile'}",
            "--no-pdf-header-footer", "--print-to-pdf-no-header",
            f"--print-to-pdf={output_pdf}", input_html.as_uri(),
        ], capture_output=True, timeout=90)
        if result.returncode or not output_pdf.is_file() or output_pdf.stat().st_size < 5:
            details = result.stderr.decode("utf-8", errors="replace")[-3000:]
            raise RuntimeError(f"Browser PDF conversion failed (exit {result.returncode}): {details}")
        with output_pdf.open("rb") as stream:
            if stream.read(5) != b"%PDF-":
                raise RuntimeError("Browser output is not a PDF")
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
        print(f"warning SHORTCUTSPDF001: {error} Skipping shortcuts PDF generation; publish will continue.")
        return 0
    try:
        convert(arguments.source.resolve(), arguments.destination.resolve(), browser)
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"Shortcuts PDF: {error}", file=sys.stderr)
        return 1
    print(f"Shortcuts PDF generated: {arguments.destination.resolve()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
