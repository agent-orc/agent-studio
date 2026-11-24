#!/usr/bin/env python3
"""Check the AGT-4032 Dossier contract without starting product services."""

import hashlib
import json
import math
import re
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


class Article(HTMLParser):
    def __init__(self, content):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.links = []
        self.sections = {}
        self.stack = []
        self.decisions = {}
        self.prompts = {}
        self.current_prompt = None
        self.feed(content)
        self.close()
        assert not self.stack, f"Unclosed sections: {self.stack}"

    def handle_starttag(self, tag, pairs):
        attrs = dict(pairs)
        if tag == "pre" and attrs.get("data-implementation-prompt"):
            self.current_prompt = attrs["data-implementation-prompt"]
            assert self.current_prompt not in self.prompts, "Duplicate implementation prompt"
            self.prompts[self.current_prompt] = ""
        if attrs.get("id"):
            assert attrs["id"] not in self.ids, f"Duplicate HTML id: {attrs['id']}"
            self.ids.add(attrs["id"])
        for name in ("href", "src"):
            if attrs.get(name):
                self.links.append(attrs[name])
        if tag == "section":
            key = attrs.get("data-concept-section")
            self.stack.append(key)
            if key:
                assert key not in self.sections, f"Duplicate section: {key}"
                self.sections[key] = ""
            if attrs.get("data-decision-id"):
                self.decisions[attrs["data-decision-id"]] = set()
                self.current_decision = attrs["data-decision-id"]
        if attrs.get("data-option-id"):
            self.decisions[self.current_decision].add(attrs["data-option-id"])

    def handle_endtag(self, tag):
        if tag == "pre":
            self.current_prompt = None
        if tag == "section":
            assert self.stack, "Unmatched closing section"
            self.stack.pop()

    def handle_data(self, data):
        if self.current_prompt is not None:
            self.prompts[self.current_prompt] += data
        for key in self.stack:
            if key:
                self.sections[key] += data


def verify(results_dir=None):
    dossier = Path(__file__).resolve().parent
    root = dossier.parent.parent
    html = (dossier / "index.html").read_text()
    article = Article(html)
    workbench = json.loads((dossier / "workbench.json").read_text())
    review_map = (dossier / "review-map.md").read_text()
    required = {"alternatives", "recommendation", "evidence", "open-decisions"}
    assert required <= article.sections.keys(), "Required Dossier section missing"
    assert all(len(article.sections[k].strip()) > 100 for k in required)
    for key in ("schemaVersion", "id", "title", "summary", "entrypoint", "status",
                "phase", "updatedAt", "sourceTaskKeys", "implementationTasks"):
        assert key in workbench, f"Missing metadata: {key}"
    assert workbench["schemaVersion"] == 1
    assert workbench["sourceTaskKeys"] == ["AGT-4032"]
    assert workbench["entrypoint"] == "index.html"
    assert workbench["status"] == "decision-pending"
    assert workbench["phase"] == "decision-ready"
    assert workbench["id"] == dossier.name

    checked_links = 0
    local_links = list(article.links)
    for document in dossier.glob("*.md"):
        local_links.extend(re.findall(r"\]\(([^)]+)\)", document.read_text()))
    for link in local_links:
        url = urlsplit(link)
        if url.scheme or url.netloc:
            continue
        target = (dossier / unquote(url.path)).resolve() if url.path else dossier / "index.html"
        assert target.is_relative_to(root), f"Link escapes repository: {link}"
        assert target.exists(), f"Missing local link: {link}"
        if url.fragment and target.suffix == ".html":
            target_article = article if target == dossier / "index.html" else Article(target.read_text())
            assert unquote(url.fragment) in target_article.ids, f"Missing HTML anchor: {link}"
        if url.fragment and target.suffix == ".md":
            headings = re.findall(r"^#+\s+(.+)$", target.read_text(), re.M)
            anchors = {re.sub(r"[^\w\s-]", "", s.lower()).replace(" ", "-") for s in headings}
            assert unquote(url.fragment) in anchors, f"Missing Markdown anchor: {link}"
        checked_links += 1

    tasks = workbench["implementationTasks"]
    assert [t["id"] for t in tasks] == [f"SL-{i}" for i in range(1, 14)]
    earlier = set()
    for i, task in enumerate(tasks):
        key = task["id"]
        assert task["title"].strip() and task["promptMarkdown"].strip()
        assert task.get("dependsOn") == ([] if i == 0 else [tasks[i - 1]["id"]])
        assert set(task["dependsOn"]) <= earlier, f"Unordered dependency: {key}"
        prompt = task["promptMarkdown"]
        assert article.prompts.get(key) == prompt, f"HTML/JSON prompt mismatch: {key}"
        for heading in ("## Scope", "## Deliver", "## Acceptance", "## Verification and result"):
            assert heading in prompt, f"Incomplete prompt: {key} {heading}"
        scope = task["acceptanceScope"]
        assert scope["slice"] == key and scope["deliveryMode"] == "bounded-slice"
        assert scope["criteria"] and all(c in prompt and c in review_map for c in scope["criteria"])
        assert f'id="{key}"' in html and f"## {key}:" in review_map
        earlier.add(key)
    assert set(article.prompts) == earlier, "HTML prompt inventory differs from JSON"

    for decision in workbench["openDecisions"]:
        key = decision["decisionId"]
        assert set(decision["options"]) == article.decisions[key]
        assert decision["recommended"] in decision["options"]
        assert decision["rationale"] in article.sections["open-decisions"]
        assert decision["recommendation"].strip() and decision["status"] == "open"
    assert len(workbench["openDecisions"]) == len(article.decisions) == 10

    template = (root / "docs/app/templates/article-document-v2.html").read_text()
    template_css = re.search(r"<style[^>]*>(.*?)</style>", template, re.S).group(1)
    assert template_css in html, "Canonical article CSS changed"
    for source in dossier.iterdir():
        if source.suffix in (".html", ".json", ".md", ".py"):
            assert "\u2014" not in source.read_text(), f"Em dash: {source.name}"

    costs = json.loads((dossier / "cost-model.json").read_text())
    assert len(costs["scenarios"]) == 3
    for scenario in costs["scenarios"]:
        tokens = scenario["addedTokensPerRequest"]
        assert scenario["tenRequestInputTokens"] == 10 * tokens
        for model, rates in costs["rates"].items():
            actual = scenario["incrementalUsd"][model]
            expected = {"uncachedRequest": tokens * rates["input"] / 1e6,
                        "writeRequest": tokens * rates["write"] / 1e6,
                        "fullHitRequest": tokens * rates["read"] / 1e6,
                        "tenRequestsNoCache": 10 * tokens * rates["input"] / 1e6,
                        "tenRequestsOneWriteNineHits": tokens * (rates["write"] + 9 * rates["read"]) / 1e6}
            assert all(math.isclose(actual[k], v, abs_tol=1e-10) for k, v in expected.items())
    ceiling = costs["classifierCeiling"]
    rate = costs["rates"][ceiling["model"]]
    assert math.isclose(ceiling["uncachedUsd"],
                        (ceiling["inputTokens"] * rate["input"] + ceiling["outputTokens"] * rate["output"]) / 1e6)

    # These are the entry files copied first by ConceptDossierReviewMaterial.
    entry_sizes = {name: len((dossier / name).read_text()) for name in ("index.html", "workbench.json")}
    assert max(entry_sizes.values()) <= 256_000 and sum(entry_sizes.values()) <= 512_000
    files = {p.name: {"bytes": p.stat().st_size, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
             for p in sorted(dossier.iterdir()) if p.is_file()}
    delivery_checks = []
    if results_dir is not None:
        results_dir = Path(results_dir).resolve()
        entry = "docs/portable-skill-library/index.html"
        for reference in (results_dir / "deliverables.md", results_dir.parent / "status.md",
                          results_dir / "status.md"):
            assert entry in reference.read_text(), f"Missing canonical task reference: {reference}"
            delivery_checks.append(str(reference))
        for name in files:
            copy = results_dir / "dossier" / name
            assert copy.read_bytes() == (dossier / name).read_bytes(), f"Reviewer copy mismatch: {name}"
        inventory = (results_dir / "implementation-cards.md").read_text()
        readable_report = (results_dir / "report.md").read_text()
        assert (results_dir / "report.html").read_bytes() == (dossier / "index.html").read_bytes(), \
            "Collected HTML report differs from the canonical entrypoint"
        sections = (results_dir / "required-sections.md").read_text()
        for key in required:
            assert article.sections[key].strip() in sections, f"Missing collected section text: {key}"
        for task in tasks:
            assert task["promptMarkdown"] in inventory, f"Missing collected full prompt: {task['id']}"
            assert task["promptMarkdown"] in readable_report, f"Missing readable report prompt: {task['id']}"
        delivery_checks.append("All Dossier files copied byte-for-byte; all full prompts collected")
        delivery_checks.append("Complete HTML/text reports and verbatim required section extracts present")
    return {"status": "pass", "sourceTaskKey": "AGT-4032", "requiredSections": sorted(required),
            "implementationTasks": [{"id": t["id"], "dependsOn": t["dependsOn"],
                                     "acceptanceCriteria": len(t["acceptanceScope"]["criteria"])} for t in tasks],
            "openDecisionsWithRecommendations": len(article.decisions), "checkedLocalLinks": checked_links,
            "costScenariosRecalculated": 3, "canonicalTemplateCss": "exact match",
            "htmlPromptsMatchingJson": len(article.prompts), "deliveryChecks": delivery_checks,
            "entrypointOnlyProof": {
                "requiredSectionCharacters": {k: len(article.sections[k].strip()) for k in sorted(required)},
                "completePromptIds": list(article.prompts),
                "instruction": "The HTML entrypoint and descriptor contain the complete decision and card contract; linked source files provide provenance."},
            "reviewEntryCharacters": entry_sizes, "files": files,
            "limits": "Structural and arithmetic checks; no skill-effect experiment, live CLI probe or operator approval."}


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-dir", type=Path, help="Also verify task references and the collected reviewer copy")
    args = parser.parse_args()
    print(json.dumps(verify(args.results_dir), indent=2))
