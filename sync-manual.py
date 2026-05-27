#!/usr/bin/env python3
"""
Sync MANUAL.md from readme.html.

readme.html is the canonical user manual — bundled inside RemSound and opened by F1.
MANUAL.md is the GitHub-facing Markdown rendition shown on the repository page.

This script reads readme.html, converts it to Markdown via html2text, and post-processes
the output to fix two things html2text doesn't do well on this document:

  * Table of contents links. The HTML uses custom anchors like `#what-it-does` which
    html2text strips. We rebuild the TOC after conversion using GitHub's auto-generated
    heading anchors (lowercased, punctuation stripped, spaces → hyphens).
  * Cosmetic backslash escapes before periods after numbers in headings (`## 1\. ...`
    instead of `## 1. ...`). GitHub renders both the same but the un-escaped version
    looks cleaner in raw source.

The script is invoked automatically by build-release.ps1 as the first step before
packaging a release, so MANUAL.md can never get out of sync with the bundled help.
It can also be run by hand from the repo root: `python sync-manual.py`.
"""

from __future__ import annotations

import html2text
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).parent
README_HTML = REPO_ROOT / "readme.html"
MANUAL_MD = REPO_ROOT / "MANUAL.md"


def github_anchor(heading_text: str) -> str:
    """Replicate GitHub's auto-anchor rule for a Markdown heading.

    Rules (per https://gist.github.com/asabaylus/3071099):
      * Lowercase everything.
      * Replace spaces with hyphens.
      * Strip all punctuation EXCEPT hyphens and underscores.
      * Collapse runs of hyphens (though GitHub does NOT — it keeps them).
    """
    s = heading_text.strip().lower()
    # Replace whitespace with hyphens
    s = re.sub(r"\s+", "-", s)
    # Strip punctuation except hyphens and underscores
    s = re.sub(r"[^\w\-]", "", s)
    return s


def convert_html_to_markdown(html: str) -> str:
    # Strip <head> entirely — none of its content belongs in the Markdown
    html = re.sub(r"<head>.*?</head>", "", html, flags=re.DOTALL)
    # Render every <div class="..."> callout (note / warn / etc.) as a blockquote —
    # Markdown's native callout. The wildcard catches all classes in one rule so a new
    # callout class added later doesn't silently break the structure (which it did for
    # the original `<div class="note">`-only rule when `class="warn"` was introduced).
    html = re.sub(r'<div\s+class="[^"]*">', "<blockquote>", html)
    html = re.sub(r"</div>", "</blockquote>", html)

    h = html2text.HTML2Text()
    h.body_width = 0           # never hard-wrap; let the rendering layer reflow
    h.ignore_links = False
    h.unicode_snob = True
    h.use_automatic_links = True
    h.bypass_tables = False
    h.escape_snob = False
    md = h.handle(html)

    # Replace any leftover U+FFFD with the em-dash that almost always belongs there
    md = md.replace("�", "—")
    # Strip cosmetic backslash escapes before periods after numbers in headings
    md = re.sub(
        r"^(##+ \d+)\\\.",
        lambda m: m.group(1) + ".",
        md,
        flags=re.MULTILINE,
    )
    # Trim trailing whitespace on every line
    md = "\n".join(line.rstrip() for line in md.splitlines()) + "\n"
    return md


def rebuild_toc(md: str) -> str:
    """Rebuild the Table of contents section using GitHub-style anchor links.

    The HTML version of readme.html uses a hand-written `<ol>` with `<a href="#id">` items
    where `id` matches the `id=""` attribute on later `<h2>` elements. html2text drops those
    links entirely on conversion. Here we regenerate the list from the actual `## ` headings
    in the post-conversion Markdown, so the TOC always reflects the real document.
    """
    # Collect all top-level (##) headings in document order, skipping the TOC heading itself
    headings: list[str] = []
    for line in md.splitlines():
        m = re.match(r"^##\s+(.+?)\s*$", line)
        if not m:
            continue
        text = m.group(1).strip()
        if text.lower() == "table of contents":
            continue
        headings.append(text)

    if not headings:
        return md

    # Build the new TOC: numbered list with anchor links to each heading
    toc_lines = []
    for h_text in headings:
        # Strip the leading "N. " from headings like "1. What RemSound does" so the displayed
        # link text in the TOC reads naturally. The anchor still references the FULL heading.
        display = re.sub(r"^\d+\.\s*", "", h_text)
        anchor = github_anchor(h_text)
        toc_lines.append(f"  1. [{display}](#{anchor})")
    new_toc_block = "\n".join(toc_lines) + "\n"

    # Replace the existing TOC block (everything between "## Table of contents" and the next "## ")
    # with the regenerated one. Using a callback to keep the surrounding markers intact.
    def replace_toc(match: re.Match[str]) -> str:
        return match.group(1) + "\n\n" + new_toc_block + "\n"

    pattern = re.compile(
        r"(##\s+Table of contents\s*\n)(?:.*?)(?=\n##\s)",
        flags=re.DOTALL,
    )
    if not pattern.search(md):
        # Defensive: TOC section wasn't found in the expected shape — leave the doc alone.
        return md
    return pattern.sub(replace_toc, md, count=1)


def main() -> int:
    if not README_HTML.exists():
        print(f"ERROR: {README_HTML} not found", file=sys.stderr)
        return 1
    html = README_HTML.read_text(encoding="utf-8")
    md = convert_html_to_markdown(html)
    md = rebuild_toc(md)
    MANUAL_MD.write_text(md, encoding="utf-8", newline="\n")
    print(f"OK - MANUAL.md regenerated from readme.html ({len(md):,} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
