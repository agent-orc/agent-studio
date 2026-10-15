#!/usr/bin/env python3
"""Focused evidence contract tests for candidate installation scenarios."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import hashlib


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


if __name__ == "__main__":
    unittest.main()
