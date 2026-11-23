#!/usr/bin/env python3
"""Reproduces what the concept-fit review receives of this Dossier, so that nothing a
reviewer must read is cut. Two inputs, ported from the product code; keep them in step:

1. The snapshot of index.html, workbench.json and every file they link
   (backend/Features/Runner/ConceptDossierReviewMaterial.cs). The Studio reads it from
   its own checkout, which holds a Dossier only after integration.
2. The diff of the delivery against the integration branch
   (runner/SourceFacts/MergeBaseDiffMaterial.cs), cut a file and in total
   (runner/ReviewAspectInputBudget.cs). On a first delivery this is the only place a
   reviewer sees the text.

A problem is any Dossier file that would be missing, cut, over a budget or not
reached from index.html, any link that leaves this directory (it would pull another
Dossier into the snapshot), and any change outside this directory.

Usage: python3 -I docs/dispatcher-personas/review_input.py [--base <ref>]
Exit code 1 on a problem. Standard library only.
"""
import html
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
DIRECTORY = "docs/dispatcher-personas"
LIMITS = {"files": 64, "fileCharacters": 256_000, "totalCharacters": 512_000,
          "diffFiles": 200, "diffLines": 12_000, "diffFileCharacters": 40_000, "diffCharacters": 580_000}
# A margin under the per-file diff cut, so that a small follow-up edit cannot push a file over it.
DIFF_FILE_TARGET = 38_000
TEXT = {".html", ".htm", ".md", ".json", ".txt", ".css", ".js", ".svg", ".csv", ".yml", ".yaml"}
LINK = re.compile(r"""(?:href|src)\s*=\s*["'](?P<html>[^"']+)["']|\]\((?P<markdown>[^)]+)\)""", re.I)


def resolve(source, target):
    target = html.unescape(target).strip()
    marker = min([i for i in (target.find("#"), target.find("?")) if i >= 0], default=-1)
    if marker >= 0:
        target = target[:marker]
    if not target or ":" in target or target.startswith("/"):
        return None
    target = urllib.parse.unquote(target).replace("\\", "/")
    combined = target if target.startswith("docs/") else source[:source.rfind("/")].rstrip("/") + "/" + target
    parts = []
    for part in combined.split("/"):
        if part in ("", "."):
            continue
        if part == "..":
            if not parts:
                return None
            parts.pop()
        else:
            parts.append(part)
    return "/".join(parts)


def snapshot():
    """Input 1: the breadth-first walk over linked files with its three limits."""
    pending = [DIRECTORY + "/index.html", DIRECTORY + "/workbench.json"]
    seen, files, problems = set(), [], []
    remaining = LIMITS["totalCharacters"]
    while pending:
        if len(seen) >= LIMITS["files"]:
            problems.append(f"snapshot: file limit {LIMITS['files']} reached; queued links were not inspected")
            break
        relative = pending.pop(0)
        if relative in seen:
            continue
        seen.add(relative)
        if not relative.startswith(DIRECTORY + "/"):
            problems.append(f"snapshot: {relative} is outside the Dossier; name other Dossiers by path, do not link them")
            continue
        full = os.path.join(ROOT, relative)
        if not os.path.isfile(full) or os.path.islink(full):
            problems.append(f"snapshot: {relative}: linked file is missing")
            continue
        size = os.path.getsize(full)
        if size > LIMITS["fileCharacters"] * 4 or os.path.splitext(relative)[1].lower() not in TEXT:
            files.append((relative, "named", size))
            continue
        content = open(full, encoding="utf-8").read()
        if len(content) > LIMITS["fileCharacters"] or len(content) > remaining:
            problems.append(f"snapshot: {relative}: {len(content)} characters exceed the remaining {remaining}")
            continue
        remaining -= len(content)
        files.append((relative, "text", len(content)))
        for match in LINK.finditer(content):
            link = resolve(relative, match.group("html") or match.group("markdown"))
            if link and link not in seen:
                pending.append(link)
    reached = {path for path, _, _ in files}
    for path in dossier_files():
        if path not in reached and path not in seen and not os.path.basename(path).startswith("."):
            problems.append(f"snapshot: {path} is not reached from index.html or workbench.json")
    return files, problems, LIMITS["totalCharacters"] - remaining


def git(*args, env=None):
    return subprocess.run(["git", *args], cwd=ROOT, env={**os.environ, **(env or {})}, check=True,
                          capture_output=True, text=True).stdout


def dossier_files():
    """Files of this directory a delivery carries: tracked or untracked, not ignored."""
    try:
        out = git("ls-files", "--cached", "--others", "--exclude-standard", "--", DIRECTORY)
        return sorted({p for p in out.splitlines() if p and os.path.isfile(os.path.join(ROOT, p))})
    except (OSError, subprocess.CalledProcessError):
        found = []
        for base, dirs, names in os.walk(os.path.join(ROOT, DIRECTORY)):
            dirs[:] = [d for d in dirs if d != "__pycache__"]
            found += [os.path.relpath(os.path.join(base, n), ROOT).replace(os.sep, "/") for n in names
                      if not n.endswith(".pyc")]
        return sorted(found)


PINNED = ["-c", "core.quotePath=false", "-c", "diff.noprefix=false", "-c", "diff.mnemonicPrefix=false",
          "-c", "diff.relative=false", "-c", "diff.algorithm=myers", "-c", "diff.indentHeuristic=true",
          "-c", "color.ui=never"]


def delivery_diff(base_ref):
    """Input 2: the working state of the checkout against the merge base, cut as the review runner cuts it."""
    base = git("merge-base", "HEAD", base_ref).strip()
    temp = tempfile.mkdtemp(prefix="dispatcher-review-")
    try:
        index = os.path.join(temp, "index")
        shutil.copyfile(os.path.join(ROOT, git("rev-parse", "--git-path", "index").strip()), index)
        env = {"GIT_INDEX_FILE": index}
        git("add", "-A", "--", ".", env=env)
        tree = git("write-tree", env=env).strip()
        changed = [p for p in git(*PINNED, "diff", "--name-only", "--find-renames", "--no-color", base, tree).splitlines() if p]
        diff = git(*PINNED, "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--full-index", "--find-renames",
                   "--unified=3", "--inter-hunk-context=0", base, tree)
    finally:
        shutil.rmtree(temp, ignore_errors=True)
    problems = []
    outside = [p for p in changed if not p.startswith(DIRECTORY + "/")]
    if outside:
        problems.append("diff: a concept delivery changes only " + DIRECTORY + "/: " + ", ".join(outside))
    lines = diff.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if len(changed) > LIMITS["diffFiles"]:
        problems.append(f"diff: {len(changed)} changed files exceed {LIMITS['diffFiles']}")
    if len(lines) > LIMITS["diffLines"]:
        problems.append(f"diff: {len(lines)} lines exceed {LIMITS['diffLines']}; the tail is dropped")
    sections = []
    for line in lines[:LIMITS["diffLines"]]:
        if line.startswith("diff --git "):
            sections.append([line[line.rfind(" b/") + 3:].strip(), ""])
        if sections:
            sections[-1][1] += line + "\n"
    first = lambda path: bool(re.search(r"\.(html|scss|css|sass)$", path, re.I))
    sections.sort(key=lambda s: (not first(s[0]), s[0]))
    used, files = 0, []
    for path, text in sections:
        available = min(LIMITS["diffFileCharacters"] - 1, LIMITS["diffCharacters"] - used - 1)
        kept = 0 if available < 300 else min(len(text), available)
        used += kept
        binary = bool(re.search(r"^Binary files ", text, re.M)) and not re.search(r"^@@ ", text, re.M)
        files.append((path, len(text), kept, binary))
        if kept < len(text):
            problems.append(f"diff: {path}: {len(text) - kept} of {len(text)} characters would be cut")
        elif len(text) > DIFF_FILE_TARGET and not binary:
            problems.append(f"diff: {path}: {len(text)} characters, above the {DIFF_FILE_TARGET} margin under the cut")
    return base, files, problems, len(changed), len(lines), used


def main(argv):
    base_ref = argv[argv.index("--base") + 1] if "--base" in argv else "origin/develop"
    files, problems, characters = snapshot()
    print("Snapshot of linked files (read from a checkout that holds the Dossier)")
    for path, kind, size in files:
        print(f"{size:>8}  {'text ' if kind == 'text' else 'named'}  {path}")
    print(f"{len(files)} of {LIMITS['files']} files, {characters} of {LIMITS['totalCharacters']} characters\n")
    try:
        base, diff_files, diff_problems, changed, lines, used = delivery_diff(base_ref)
        print(f"Delivery diff against {base[:9]}, in the order the budget takes them")
        for path, length, kept, binary in diff_files:
            state = "binary" if binary else "whole" if kept == length else f"cut to {kept}"
            print(f"{length:>8}  {state:<6}  {path}")
        print(f"{changed} of {LIMITS['diffFiles']} files, {lines} of {LIMITS['diffLines']} lines, "
              f"{used} of {LIMITS['diffCharacters']} characters")
        problems += diff_problems
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"Delivery diff not computed (no git checkout with {base_ref}): {error}")
    for problem in problems:
        print("PROBLEM " + problem)
    print("review input:", "ok" if not problems else f"{len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
