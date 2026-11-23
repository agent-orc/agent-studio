#!/usr/bin/env python3
"""Estimate how many source folders one failing test class can reach.

Model-free proxy used by the red-test-isolation Dossier (AGT-3988): a test file
reaches the folder of every repository type it names (identifiers of at least
eight characters that match a source file name; types defined in more than two
folders are ignored as ambiguous). This is a static name match, not runtime
coverage. Run from the repository root: python3 -I docs/red-test-isolation/evidence/test-folder-reach.py
"""
import glob
import os
import re
import statistics

SOURCE_ROOTS = ["backend", "runner", "task-server", "orchestration-decisions",
                "contracts", "cli-hosting", "retention"]
TEST_PROJECTS = ["backend.Tests", "runner.Tests", "task-server.Tests"]
HUB_TYPES = {"Program", "ProjectSettings", "TaskStates", "TaskJob", "RoutePermissionCatalog"}
IDENT = re.compile(r"\b([A-Z][A-Za-z0-9]{7,})\b")


def cs_files(root):
    return [f for f in glob.glob(root + "/**/*.cs", recursive=True)
            if "/bin/" not in f and "/obj/" not in f]


def main():
    types = {}
    for root in SOURCE_ROOTS:
        for path in cs_files(root):
            name = os.path.basename(path)[:-3].split(".")[0]
            types.setdefault(name, set()).add(os.path.dirname(path))
    folders_total = len({d for dirs in types.values() for d in dirs})
    print(f"source folders total {folders_total}")
    for project in TEST_PROJECTS:
        files = cs_files(project)
        counts, unmapped = [], 0
        for path in files:
            text = open(path, encoding="utf-8", errors="ignore").read()
            reached = set()
            for ident in set(IDENT.findall(text)) - HUB_TYPES:
                dirs = types.get(ident)
                if dirs and len(dirs) <= 2:
                    reached |= dirs
            if reached:
                counts.append(len(reached))
            else:
                unmapped += 1
        counts.sort()
        p90 = counts[int(0.9 * (len(counts) - 1))]
        print(f"{project} files {len(files)} mapped {len(counts)} unmapped {unmapped} "
              f"median folders {statistics.median(counts)} p90 {p90} max {counts[-1]}")


if __name__ == "__main__":
    main()
