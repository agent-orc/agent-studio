#!/usr/bin/env python3
"""Collect this documentation Dossier in the task's explicitly supplied results folder."""

import argparse
import hashlib
import json
import re
import runpy
import shutil
from html.parser import HTMLParser
from pathlib import Path


class ReadableArticle(HTMLParser):
    """Keep complete article prose and preformatted implementation prompts."""

    def __init__(self, source):
        super().__init__(convert_charrefs=True)
        self.main = False
        self.pre = False
        self.parts = []
        self.feed(source)
        self.close()

    def handle_starttag(self, tag, attrs):
        if tag == "main":
            self.main = True
        if not self.main:
            return
        if re.fullmatch(r"h[1-6]", tag):
            self.parts.append("\n\n" + "#" * int(tag[1]) + " ")
        elif tag == "pre":
            self.parts.append("\n\n````text\n")
            self.pre = True
        elif tag in {"p", "tr", "summary", "figcaption", "div"}:
            self.parts.append("\n\n")
        elif tag == "li":
            self.parts.append("\n- ")
        elif tag in {"td", "th"}:
            self.parts.append(" | ")
        elif tag == "br":
            self.parts.append("\n")

    def handle_endtag(self, tag):
        if tag == "pre":
            self.parts.append("\n````\n\n")
            self.pre = False
        if tag == "main":
            self.main = False

    def handle_data(self, data):
        if self.main:
            self.parts.append(data if self.pre else re.sub(r"\s+", " ", data))


def collect(results):
    dossier = Path(__file__).resolve().parent
    root = dossier.parent.parent
    assert results.is_absolute(), "Supply an absolute task results directory"
    results = results.resolve()
    assert not results.is_relative_to(root), "Review artifacts belong outside the repository"
    assert results.name == "results" and re.fullmatch(r"AGT-\d+", results.parent.name), "Wrong task results folder"
    task_key = results.parent.name
    results.mkdir(parents=True, exist_ok=True)
    checks = runpy.run_path(str(dossier / "verify.py"))
    checks["verify"]()
    before = results / "verification-before.json"
    if not before.exists():
        try:
            checks["verify"](results)
            initial = {"status": "pass", "check": "collected-review-material"}
        except (AssertionError, FileNotFoundError) as exc:
            initial = {"status": "fail", "check": "collected-review-material",
                       "observedFailure": str(exc)}
        initial["limit"] = "Current results-directory observation; earlier review inputs were not available."
        before.write_text(json.dumps(initial, indent=2) + "\n")
    source = (dossier / "index.html").read_text()
    article = checks["Article"](source)
    workbench = json.loads((dossier / "workbench.json").read_text())
    entry = "docs/portable-skill-library/index.html"

    # Copy only this Dossier, preserving its canonical repository-relative identity.
    snapshot = results / "dossier"
    snapshot.mkdir(exist_ok=True)
    for path in sorted(dossier.iterdir()):
        if path.is_file():
            shutil.copyfile(path, snapshot / path.name)
    shutil.copyfile(dossier / "index.html", results / "report.html")
    for name in ("workbench.json", "review-map.md", "evidence.md", "cost-model.json"):
        shutil.copyfile(dossier / name, results / name)
    readable = ReadableArticle(source)
    (results / "report.md").write_text(
        f"Canonical repository Dossier: `{entry}`. Complete readable article, including all thirteen prompts.\n\n"
        + "".join(readable.parts).strip() + "\n")
    sections = [f"# Required Dossier sections\n\nCanonical source: `{entry}`. Verbatim HTML text extracts.\n"]
    for key in ("alternatives", "recommendation", "evidence", "open-decisions"):
        sections.append(f"\n## {key}\n\n{article.sections[key].strip()}\n")
    (results / "required-sections.md").write_text("".join(sections))
    section_dir = results / "sections"
    section_dir.mkdir(exist_ok=True)
    for key in ("alternatives", "recommendation", "evidence", "open-decisions"):
        (section_dir / f"{key}.md").write_text(f"# {key}\n\n{article.sections[key].strip()}\n")
    cards = [f"# Complete implementation cards\n\nCanonical source: `{entry}` and its workbench.json.\n\n"]
    card_dir = results / "cards"
    card_dir.mkdir(exist_ok=True)
    for task in workbench["implementationTasks"]:
        card = f"Scheduler id: {task['id']}; dependsOn: {json.dumps(task['dependsOn'])}\n\n" + task["promptMarkdown"] + "\n\n"
        cards.append(card)
        (card_dir / f"{task['id']}.md").write_text(card)
    (results / "implementation-cards.md").write_text("".join(cards))
    review = [f"# {task_key}: review entry\n\nSource card: AGT-4032. Canonical Dossier: `{entry}`.\n\n",
              "The required content is reproduced below in small, individually inspectable files. "
              "The complete HTML and descriptor also contain all four sections and thirteen full prompts.\n\n",
              "## Required content\n\n| Section | Actual text | Characters |\n| --- | --- | ---: |\n"]
    for key in ("alternatives", "recommendation", "evidence", "open-decisions"):
        review.append(f"| {key} | [Read section](sections/{key}.md) | {len(article.sections[key].strip())} |\n")
    review.append("\n## Complete card sequence\n\n| Id | Title and full prompt | dependsOn |\n| --- | --- | --- |\n")
    for task in workbench["implementationTasks"]:
        review.append(f"| {task['id']} | [{task['title']}](cards/{task['id']}.md) | {', '.join(task['dependsOn']) or 'none'} |\n")
    review.append("\n[Complete HTML](report.html), [readable report](report.md), [descriptor](workbench.json), "
                  "[requirement map](review-map.md), [verification](verification.json), "
                  "[file hashes](review-manifest.json).\n\n"
                  "The decisions remain recommendations. This repair restores reviewable concept content; "
                  "it does not implement the thirteen proposed product slices.\n")
    (results / "review-entry.md").write_text("".join(review))
    deliverables = f"""# Deliverables

<!-- agent-studio:concept-dossier -->
## Dossier

- Path: `{entry}`
<!-- /agent-studio:concept-dossier -->

The repository Dossier is canonical. Status: decision-pending; phase: decision-ready.
All eight brief topics, four marked required sections, ten recommended decisions
and thirteen complete implementation cards are present. The dependency chain is
SL-1 -> SL-2 -> SL-3 -> SL-4 -> SL-5 -> SL-6 -> SL-7 -> SL-8 -> SL-9 -> SL-10 -> SL-11 -> SL-12 -> SL-13.

- [Complete readable Dossier](report.md): all analysis and every full card prompt.
- [Review entry](review-entry.md): required sections and full card sequence with individual file links.
- [HTML entrypoint](report.html): byte-identical to the canonical entrypoint.
- [Required sections](required-sections.md): actual text, with contract markers.
- [All implementation cards](implementation-cards.md): complete prompts and dependency arrays.
- [Descriptor](workbench.json): authoritative metadata and implementationTasks array.
- [Review map](review-map.md): every requirement and all nine AGT-3465 predecessor slices.
- [Canonical snapshot](dossier/index.html): byte-identical copies of every Dossier file.
- [Verification](verification.json): structure, links, arithmetic and collected-byte checks.
- [File manifest](review-manifest.json): canonical identity, sizes and SHA-256 hashes.
- [Initial delivery check](verification-before.json): observed collection state before packaging.

The Dossier and its documentation-index entry change in the repository. Product source and
configuration are untouched. Document verification does not establish skill
effectiveness, live CLI transport or operator approval. External source links
refer to the repository or official documentation; those sources are not bundled.
"""
    if (results / "checks.md").exists():
        deliverables += "\n[Run verification](checks.md) records the build, focused tests, guards and lint.\n"
    if (results / "integration-recovery.md").exists():
        deliverables += "\n[Integration recovery](integration-recovery.md) records the failed stage and file resolutions.\n"
    if (results / "document-verification.json").exists():
        deliverables += "\n## Document checks\n\n[Rendering verification](document-verification.json) records desktop and phone checks in both themes.\n\n"
        for capture in sorted((results / "captures").glob("*.png")):
            relative = capture.relative_to(results).as_posix()
            deliverables += f"- [{capture.stem}]({relative})\n"
        deliverables += "\nThese captures show the local Dossier document, not implemented product surfaces.\n"
    (results / "deliverables.md").write_text(deliverables)
    status = f"""# {task_key}

<!-- agent-studio:concept-dossier -->
## Dossier

- Path: `{entry}`
<!-- /agent-studio:concept-dossier -->

Source card: AGT-4032. The collected report contains the complete Dossier and all thirteen card prompts.
deliverables.md and review-entry.md index the canonical snapshot, individual section extracts,
all full card prompts, verification.json and review-manifest.json.
Status decision-pending; phase decision-ready. No operator approval is claimed.
"""
    # Task evidence stays entirely inside the explicitly supplied results directory.
    (results / "status.md").write_text(status)
    verification = checks["verify"](results)
    (results / "verification.json").write_text(json.dumps(verification, indent=2) + "\n")
    files = {}
    for path in sorted(results.rglob("*")):
        if path.is_file() and path.name != "review-manifest.json":
            assert path.stat().st_size <= 8 * 1024 * 1024, f"Artifact too large: {path.name}"
            files[path.relative_to(results).as_posix()] = {
                "bytes": path.stat().st_size,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            }
    manifest = {"sourceTaskKey": "AGT-4032", "deliveryTaskKey": task_key, "canonicalEntrypoint": entry,
                "dossierUpdatedAt": workbench["updatedAt"], "files": files,
                "limits": "Collected bytes verified locally; later reviewer ingestion and approval are not claimed."}
    (results / "review-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    assert sum(p.stat().st_size for p in results.rglob("*") if p.is_file()) <= 100 * 1024 * 1024
    print(json.dumps({"status": "pass", "resultsDirectory": str(results),
                      "canonicalEntrypoint": entry, "collectedFiles": len(files) + 1,
                      "implementationTasks": len(workbench["implementationTasks"])}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-dir", type=Path, required=True)
    collect(parser.parse_args().results_dir)
