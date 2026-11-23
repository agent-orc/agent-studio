#!/usr/bin/env python3
"""Share of recent integrations that a hold would have stopped, by hold granularity.

Used by the red-test-isolation Dossier (AGT-3988). Reads the first-parent commits
of HEAD in a date window and their changed paths, then compares two holds:

* area hold: the post-merge suite area of a backend test (backend/, backend.Tests/
  and the shared .NET libraries, as PostMergeRunPlan selects backend-routine);
* covering-folder hold: for every backend.Tests file, the folders of the
  repository types it names (same static rule as test-folder-reach.py, exact
  directory match) plus the test file itself. Files that name no type, or more
  than 12 folders, fall back to the area hold.

It prints the share of integrations each hold would have stopped; for the
covering-folder hold, the median and p90 over all backend test files.
Run from the repository root:
python3 -I docs/red-test-isolation/evidence/hold-reach.py --since 2026-11-16 --until 2026-11-23
"""
import argparse
import glob
import os
import re
import statistics
import subprocess

AREA = ("backend/", "backend.Tests/", "contracts/", "orchestration-decisions/",
        "cli-hosting/", "retention/", "testsupport/")
SOURCE_ROOTS = ["backend", "runner", "task-server", "orchestration-decisions",
                "contracts", "cli-hosting", "retention"]
HUB_TYPES = {"Program", "ProjectSettings", "TaskStates", "TaskJob", "RoutePermissionCatalog"}
IDENT = re.compile(r"\b([A-Z][A-Za-z0-9]{7,})\b")
FOLDER_LIMIT = 12


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout


def cs_files(root):
    return [f for f in glob.glob(root + "/**/*.cs", recursive=True)
            if "/bin/" not in f and "/obj/" not in f]


def covering(types, path):
    text = open(path, encoding="utf-8", errors="ignore").read()
    reached = set()
    for ident in set(IDENT.findall(text)) - HUB_TYPES:
        dirs = types.get(ident)
        if dirs and len(dirs) <= 2:
            reached |= dirs
    return reached


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--since", required=True)
    parser.add_argument("--until", required=True)
    a = parser.parse_args()
    commits = git("log", "--first-parent", "--format=%H", f"--since={a.since}",
                  f"--until={a.until}", "HEAD").split()
    changes = []
    for sha in commits:
        paths = [p for p in git("diff", "--name-only", sha + "^", sha).splitlines() if p]
        if paths:
            changes.append(paths)
    total = len(changes)

    def area_hit(paths):
        return any(p.startswith(AREA) for p in paths)

    area = sum(1 for paths in changes if area_hit(paths))
    types = {}
    for root in SOURCE_ROOTS:
        for path in cs_files(root):
            types.setdefault(os.path.basename(path)[:-3].split(".")[0], set()).add(os.path.dirname(path))
    shares, fallback = [], 0
    for test in cs_files("backend.Tests"):
        folders = covering(types, test)
        if not folders or len(folders) > FOLDER_LIMIT:
            fallback += 1
            hits = area
        else:
            hits = sum(1 for paths in changes
                       if any(p == test or os.path.dirname(p) in folders for p in paths))
        shares.append(100 * hits / max(total, 1))
    shares.sort()
    p90 = shares[int(0.9 * (len(shares) - 1))]
    print(f"first-parent integrations with changes {total} ({a.since} to {a.until})")
    print(f"area hold (backend-routine): {area} ({100 * area / max(total, 1):.0f}%)")
    print(f"covering-folder hold over {len(shares)} backend test files "
          f"({fallback} fall back to the area): median {statistics.median(shares):.0f}%, p90 {p90:.0f}%")


if __name__ == "__main__":
    main()
