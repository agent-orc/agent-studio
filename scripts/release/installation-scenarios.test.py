#!/usr/bin/env python3
"""Focused evidence contract tests for candidate installation scenarios."""

import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import hashlib
import sys
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("installation-scenarios.py")
SPEC = importlib.util.spec_from_file_location("installation_scenarios", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class InstallationEvidenceTests(unittest.TestCase):
    def test_required_scenario_without_previous_candidate_fails_before_install(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            candidate = root / "candidate"
            candidate.mkdir()
            for name in ("agent-studio-setup", "agent-orchestrator-1.2.3-linux-x64.tar.gz"):
                (candidate / name).write_text(name, encoding="ascii")
            (candidate / "SHA256SUMS").write_text("".join(
                f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n"
                for path in candidate.iterdir()), encoding="ascii")
            arguments = [str(SCRIPT), "--scenario", "linux-native",
                         "--candidate-dir", str(candidate), "--report-dir", str(root / "reports")]
            with patch.object(sys, "argv", arguments):
                self.assertEqual(1, MODULE.main())
            report = json.loads((root / "reports" / "linux-native.json").read_text())
            self.assertEqual("failed", report["status"])
            self.assertIn("--previous-candidate-dir", report["steps"][-1]["reason"])

    def test_lifecycle_needs_real_update_and_retained_card(self):
        with tempfile.TemporaryDirectory() as temporary:
            proof = Path(temporary) / "lifecycle.json"
            complete = {"status": "passed", "installationId": "installation-one",
                        "previousVersion": "1.2.2", "candidateVersion": "1.2.3",
                        "updatedFromPrevious": True, "uninstalledAndReinstalled": True,
                        "retainedCard": True}
            proof.write_text(json.dumps(complete), encoding="utf-8")
            self.assertEqual("passed", MODULE.retained_lifecycle_step(proof)["status"])
            proof.write_text(json.dumps({**complete, "updatedFromPrevious": False}), encoding="utf-8")
            self.assertEqual("failed", MODULE.retained_lifecycle_step(proof)["status"])
            proof.write_text(json.dumps({**complete, "previousVersion": "1.2.3"}), encoding="utf-8")
            self.assertEqual("failed", MODULE.retained_lifecycle_step(proof)["status"])

    def test_installed_card_evidence_requires_all_roles_and_published_sha(self):
        with tempfile.TemporaryDirectory() as temporary:
            proof = Path(temporary) / "card.json"
            complete = {"status": "passed", "state": "5-human-review",
                        "initialSha": "a" * 40, "integrationSha": "b" * 40,
                        "runnerRoles": ["coding", "review", "integration"]}
            proof.write_text(json.dumps(complete), encoding="utf-8")
            self.assertEqual("passed", MODULE.installed_card_step(proof, "Card")["status"])
            for changed in (
                {**complete, "integrationSha": complete["initialSha"]},
                {**complete, "runnerRoles": ["coding", "review"]},
                {**complete, "state": "4-auto-review"},
            ):
                proof.write_text(json.dumps(changed), encoding="utf-8")
                self.assertEqual("failed", MODULE.installed_card_step(proof, "Card")["status"])

    def test_verified_candidate_and_report_do_not_include_secret_environment(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "agent-studio-setup").write_text("setup", encoding="ascii")
            (root / "agent-orchestrator-1.2.3-linux-x64.tar.gz").write_text("archive", encoding="ascii")
            lines = [
                f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}"
                for path in root.iterdir()
            ]
            (root / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="ascii")
            self.assertEqual(2, MODULE.verify_candidate(root, False)["assetCount"])
            report_dir = root / "reports"
            self.assertTrue(MODULE.write_report(report_dir, "linux-native", root,
                                                [{"step": "Install", "status": "passed", "durationSeconds": 1}]))
            report = json.loads((report_dir / "linux-native.json").read_text())
            self.assertEqual("passed", report["status"])
            self.assertIn("<table>", (report_dir / "linux-native.html").read_text())
            (root / "agent-studio-setup").write_text("tampered", encoding="ascii")
            with self.assertRaisesRegex(ValueError, "checksum failed"):
                MODULE.verify_candidate(root, False)

    def test_failed_step_fails_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertFalse(MODULE.write_report(root, "upgrade", root,
                                                 [{"step": "Upgrade", "status": "failed", "durationSeconds": 0}]))
            self.assertEqual("failed", json.loads((root / "upgrade.json").read_text())["status"])

    def test_first_release_upgrade_is_recorded_as_not_applicable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertTrue(MODULE.write_report(root, "upgrade", root,
                                                [{"step": "Verify candidate", "status": "passed", "durationSeconds": 0},
                                                 {"step": "Previous release", "status": "not_applicable",
                                                  "durationSeconds": 0}]))
            self.assertEqual("not_applicable", json.loads((root / "upgrade.json").read_text())["status"])

    def test_missing_provider_credential_is_a_visible_skip(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertTrue(MODULE.write_report(root, "simulated-user", root,
                                                [{"step": "AI user", "status": "skipped",
                                                  "reason": "provider secret absent", "durationSeconds": 0}]))
            self.assertEqual("skipped", json.loads((root / "simulated-user.json").read_text())["status"])

    def test_simulated_friction_is_escaped_in_html(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "simulated-user-friction.json").write_text(json.dumps({
                "friction": [{"location": "install.md", "guess": "<missing>", "evidence": "line 1"}]
            }))
            self.assertTrue(MODULE.write_report(root, "simulated-user", root,
                                                [{"step": "AI user", "status": "passed", "durationSeconds": 1}]))
            page = (root / "simulated-user.html").read_text()
            self.assertIn("&lt;missing&gt;", page)
            self.assertNotIn("<missing>", page)

    def test_unfinished_journey_fails_gate_with_visible_reason(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            summary = root / "summary.md"
            with patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": str(summary)}):
                self.assertFalse(MODULE.write_report(root, "linux-native", root, [
                    {"step": "Install", "status": "passed", "durationSeconds": 1},
                    MODULE.pending_step("linux-native-installed-card", "Integration", "Awaiting first-use journey"),
                ]))
            report = json.loads((root / "linux-native.json").read_text())
            self.assertEqual("failed", report["status"])
            self.assertEqual("failed", report["steps"][1]["status"])
            self.assertIn("Awaiting first-use journey", (root / "linux-native.html").read_text())
            self.assertIn("gate failed", summary.read_text())

    def test_delivered_check_failure_fails_with_unfinished_step(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertFalse(MODULE.write_report(root, "linux-native", root, [
                {"step": "Install", "status": "failed", "durationSeconds": 1},
                MODULE.pending_step("linux-native-installed-card", "Integration", "Awaiting first-use journey"),
            ]))
            self.assertEqual("failed", json.loads((root / "linux-native.json").read_text())["status"])

    def test_every_unfinished_step_is_strict(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            step = MODULE.pending_step("new-installed-step", "First use", "Not implemented")
            self.assertEqual("failed", step["status"])
            self.assertFalse(MODULE.write_report(root, "linux-native", root, [step]))

    def test_required_journey_with_skipped_step_fails_gate(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertFalse(MODULE.write_report(root, "linux-native", root, [
                {"step": "Install", "status": "passed", "durationSeconds": 1},
                {"step": "Integration", "status": "skipped", "durationSeconds": 0},
            ]))
            self.assertEqual("skipped", json.loads((root / "linux-native.json").read_text())["status"])


if __name__ == "__main__":
    unittest.main()
