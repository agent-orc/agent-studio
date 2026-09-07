#!/usr/bin/env python3

import importlib.util
import json
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("deployment_scenario", HERE / "run_scenario.py")
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class ScenarioContractTests(unittest.TestCase):
    def setUp(self):
        self.manifest = json.loads((HERE / "deployment-scenario.json").read_text(encoding="utf-8"))

    def test_manifest_has_six_step_smoke_prefix_and_bound_handlers(self):
        MODULE.validate_manifest(self.manifest)
        self.assertEqual(6, self.manifest["smokeStepCount"])
        self.assertEqual("run-fake-cli", self.manifest["steps"][5]["id"])
        for step in self.manifest["steps"]:
            self.assertTrue(callable(getattr(MODULE.Scenario, step["handler"], None)))

    def test_failed_step_is_machine_readable_and_keeps_evidence_link(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            result = MODULE.StepResult("claim", "failed", 12, ["evidence/claim/failure.txt"], "claim failed")
            MODULE.write_reports(output, "scenario", "inproc", "smoke", [result], 12)
            suite = ET.parse(output / "scenario.junit.xml").getroot()
            self.assertEqual("1", suite.attrib["failures"])
            self.assertEqual("claim failed", suite.find("testcase/failure").attrib["message"])
            report = (output / "scenario-report.md").read_text(encoding="utf-8")
            self.assertIn("| claim | failed | 12 |", report)
            self.assertIn("evidence/claim/failure.txt", report)


if __name__ == "__main__":
    unittest.main()
