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
    assert results.name == "results" and results.parent.name == "AGT-4032", "Wrong task results folder"
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
    cards = [f"# Complete implementation cards\n\nCanonical source: `{entry}` and its workbench.json.\n\n"]
    for task in workbench["implementationTasks"]:
        cards.append(f"Scheduler id: {task['id']}; dependsOn: {json.dumps(task['dependsOn'])}\n\n")
        cards.append(task["promptMarkdown"] + "\n\n")
    (results / "implementation-cards.md").write_text("".join(cards))
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
- [HTML entrypoint](report.html): byte-identical to the canonical entrypoint.
- [Required sections](required-sections.md): actual text, with contract markers.
- [All implementation cards](implementation-cards.md): complete prompts and dependency arrays.
- [Descriptor](workbench.json): authoritative metadata and implementationTasks array.
- [Review map](review-map.md): every requirement and all nine AGT-3465 predecessor slices.
- [Canonical snapshot](dossier/index.html): byte-identical copies of every Dossier file.
- [Verification](verification.json): structure, links, arithmetic and collected-byte checks.
- [File manifest](review-manifest.json): canonical identity, sizes and SHA-256 hashes.
- [Initial delivery check](verification-before.json): observed collection state before packaging.

Only docs/portable-skill-library/ changes in the repository. Product source and
configuration are untouched. Document verification does not establish skill
effectiveness, live CLI transport or operator approval. External source links
refer to the repository or official documentation; those sources are not bundled.
"""
    if (results / "document-verification.json").exists():
        deliverables += "\n## Document checks\n\n[Rendering verification](document-verification.json) records desktop and phone checks in both themes.\n\n"
        for capture in sorted((results / "captures").glob("*.png")):
            relative = capture.relative_to(results).as_posix()
            deliverables += f"- [{capture.stem}]({relative})\n"
        deliverables += "\nThese captures show the local Dossier document, not implemented product surfaces.\n"
    (results / "deliverables.md").write_text(deliverables)
    status = f"""# AGT-4032

<!-- agent-studio:concept-dossier -->
## Dossier

- Path: `{entry}`
<!-- /agent-studio:concept-dossier -->

The collected report contains the complete Dossier and all thirteen card prompts.
results/deliverables.md indexes the canonical snapshot, section extracts,
implementation-cards.md, verification.json and review-manifest.json.
Status decision-pending; phase decision-ready. No operator approval is claimed.
"""
    # Both locations are explicitly requested task delivery metadata.
    (results.parent / "status.md").write_text(status)
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
    manifest = {"sourceTaskKey": "AGT-4032", "canonicalEntrypoint": entry,
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
