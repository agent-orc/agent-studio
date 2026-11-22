#!/usr/bin/env python3
"""Refreshes the generated regions of the Dossier pages (docs/dispatcher-personas).

The pages are their own source. Only the text between <!--g:STYLE--> and <!--/g-->
and between <!--g:NAV--> and <!--/g--> is written here:

- STYLE: the canonical article style of docs/app/templates/article-document-v2.html,
  minified, plus the parts of src/dossier.css whose marker the page contains.
- NAV: the list of the Dossier pages, with the current page marked.

Afterwards it runs review_input.py and fails when a file a reviewer must read
would be missing, cut or left out.

Usage: python3 -I docs/dispatcher-personas/build.py [--check]
--check writes nothing and fails when a page is not up to date.
Standard library only.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
import review_input  # noqa: E402

PAGES = [
    ("index.html", "Decision page"),
    ("role.html", "Scope, today and the role"),
    ("personas.html", "Starter personas and criteria"),
    ("replay.html", "Wave and replay"),
    ("design.html", "Mockups, cut and slices"),
]
TEMPLATE = os.path.join(ROOT, "docs", "app", "templates", "article-document-v2.html")
REGION = re.compile(r"<!--g:(?P<key>[A-Z]+)-->.*?<!--/g-->", re.S)


def minify_css(css):
    css = re.sub(r"/\*.*?\*/", "", css, flags=re.S)
    css = re.sub(r"\s+", " ", css)
    css = re.sub(r"\s*([{};,>])\s*", r"\1", css)
    css = re.sub(r":\s+", ":", css)
    return css.replace(";}", "}").strip()


def article_css():
    text = open(TEMPLATE, encoding="utf-8").read()
    start = text.index('<style data-article-template="v2">')
    return minify_css(text[text.index(">", start) + 1:text.index("</style>", start)])


def dossier_parts():
    text = open(os.path.join(HERE, "src", "dossier.css"), encoding="utf-8").read()
    pieces = re.split(r"/\* @part (.+?) \*/\n", text)[1:]
    return list(zip(pieces[0::2], pieces[1::2]))


def style_for(html, article, parts):
    own = "".join(body for marker, body in parts if marker == "always" or marker in html)
    return ('<style data-article-template="v2">/* docs/app/templates/article-document-v2.html, minified by build.py */'
            + article + "</style>\n"
            + '<style data-dossier="dispatcher-personas">/* src/dossier.css, minified by build.py */'
            + minify_css(own) + "</style>")


def nav_for(name):
    current = ' aria-current="page"'
    items = "".join(f'<li><a href="{file}"{current if file == name else ""}>{title}</a></li>'
                    for file, title in PAGES)
    return f'<nav class="chapters" aria-label="Pages of this Dossier"><ol>{items}</ol></nav>'


def render(name, html, article, parts):
    # Markers are matched on the page without its generated regions.
    authored = REGION.sub("", html)

    def fill(match):
        key = match.group("key")
        value = style_for(authored, article, parts) if key == "STYLE" else nav_for(name) if key == "NAV" else None
        if value is None:
            raise SystemExit(f"{name}: unknown region {key}")
        return f"<!--g:{key}-->{value}<!--/g-->"
    return REGION.sub(fill, html)


def main():
    check = "--check" in sys.argv
    article, parts = article_css(), dossier_parts()
    stale = []
    for name, _ in PAGES:
        path = os.path.join(HERE, name)
        html = open(path, encoding="utf-8").read()
        out = render(name, html, article, parts)
        if out != html:
            stale.append(name)
            if not check:
                open(path, "w", encoding="utf-8").write(out)
    if check and stale:
        print("build: pages not up to date: " + ", ".join(stale))
        return 1
    print("build: " + ("pages up to date" if check else f"wrote {len(stale)} of {len(PAGES)} pages"))
    return review_input.main([])


if __name__ == "__main__":
    sys.exit(main())
