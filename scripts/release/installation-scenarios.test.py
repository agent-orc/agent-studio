#!/usr/bin/env python3
"""Focused evidence contract tests for candidate installation scenarios."""

import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import hashlib
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("installation-scenarios.py")
SPEC = importlib.util.spec_from_file_location("installation_scenarios", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class InstallationEvidenceTests(unittest.TestCase):
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

    def test_unimplemented_journey_fails_gate_with_visible_reason(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            summary = root / "summary.md"
            with patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": str(summary)}):
                self.assertFalse(MODULE.write_report(root, "linux-native", root, [
                    {"step": "Install", "status": "passed", "durationSeconds": 1},
                    {"id": "linux-native-installed-card", "step": "Integration", "status": "failed",
                     "reason": "Awaiting first-use journey", "durationSeconds": 0},
                ]))
            report = json.loads((root / "linux-native.json").read_text())
            self.assertEqual("failed", report["status"])
            self.assertEqual("failed", report["steps"][1]["status"])
            self.assertIn("Awaiting first-use journey", (root / "linux-native.html").read_text())
            self.assertIn("gate failed", summary.read_text())

    def test_delivered_check_failure_fails_with_missing_step(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertFalse(MODULE.write_report(root, "linux-native", root, [
                {"step": "Install", "status": "failed", "durationSeconds": 1},
                {"id": "linux-native-installed-card", "step": "Integration", "status": "failed",
                 "reason": "Awaiting first-use journey", "durationSeconds": 0},
            ]))
            self.assertEqual("failed", json.loads((root / "linux-native.json").read_text())["status"])

    def test_deferred_step_is_strict(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            step = {"id": "new-installed-step", "step": "First use", "status": "deferred",
                    "reason": "Not implemented", "durationSeconds": 0}
            self.assertFalse(MODULE.write_report(root, "linux-native", root, [step]))

    def test_required_journey_with_skipped_step_fails_gate(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertFalse(MODULE.write_report(root, "linux-native", root, [
                {"step": "Install", "status": "passed", "durationSeconds": 1},
                {"step": "Integration", "status": "skipped", "durationSeconds": 0},
            ]))
            self.assertEqual("skipped", json.loads((root / "linux-native.json").read_text())["status"])

    def test_required_installed_journeys_have_no_deferred_step(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for scenario in ("linux-native", "linux-docker", "windows-native"):
                arguments = [str(SCRIPT), "--scenario", scenario, "--candidate-dir", str(root),
                             "--report-dir", str(root / "reports")]
                with patch.object(sys, "argv", arguments), \
                     patch.object(MODULE, "verify_candidate", return_value={"assetCount": 1,
                                                                           "setupSha256": "fixture"}), \
                     patch.object(MODULE, "run", return_value={"step": "Installed journey",
                                                                  "status": "passed", "durationSeconds": 1}):
                    self.assertEqual(0, MODULE.main(), scenario)
                report = json.loads((root / "reports" / f"{scenario}.json").read_text())
                self.assertEqual("passed", report["status"])
                self.assertEqual(["passed", "passed"], [step["status"] for step in report["steps"]])


if __name__ == "__main__":
    unittest.main()
