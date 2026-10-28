#!/usr/bin/env python3
"""Focused checks for the installed card probe's evidence boundary."""

import importlib.util
import json
from pathlib import Path
import tempfile
import types
import unittest
from unittest import mock


SCRIPT = Path(__file__).with_name("installation-card.py")
SPEC = importlib.util.spec_from_file_location("installation_card", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class InstallationCardTests(unittest.TestCase):
    def test_prepare_seals_review_plan_and_creates_ready_card(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            session = root / "owner.session"
            session.write_text("owner-session")
            args = types.SimpleNamespace(server_url="http://127.0.0.1:5071",
                                         owner_session_file=session,
                                         review_token_file=root / "review.token",
                                         integration_token_file=root / "integration.token",
                                         card_file=root / "card.json", review_shell="sh")
            calls = []

            def respond(_url, _token, path, method="GET", payload=None, owner=""):
                calls.append((path, method, payload, owner))
                if path.endswith("/principals"):
                    return {"credential": "scenario-credential"}
                if path.endswith("/tasks"):
                    return {"taskId": "task-one", "taskKey": "SCN-1", "state": "2-ready"}
                return {"version": 1}

            with mock.patch.object(MODULE, "api", side_effect=respond):
                MODULE.prepare(args, "management")
            self.assertEqual("SCN-1", json.loads(args.card_file.read_text())["taskKey"])
            self.assertEqual("owner-session", calls[1][3])
            self.assertEqual("owner-session", calls[2][3])
            self.assertEqual("PUT", calls[0][1])
            self.assertEqual(["build-tests"], calls[0][2]["plan"]["requiredAspects"])

    def test_verify_requires_review_integration_and_remote_sha(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            card = root / "card.json"
            card.write_text(json.dumps({"taskId": "task-one", "taskKey": "SCN-1"}))
            args = types.SimpleNamespace(server_url="http://127.0.0.1:5071",
                                         repository_url="git://127.0.0.1/scenario.git",
                                         card_file=card, timeout=5)
            observed = []

            def respond(_url, _token, path):
                observed.append(path)
                if path.endswith("/history"):
                    return {"runs": [{"resultSha": "abc"}],
                            "artifacts": [{"name": "scenario-run-log.txt"}]}
                if path.startswith("/api/v1/integrations?"):
                    return [{"status": "integrated", "lastOutcome": "integrated",
                             "integratedSha": "def", "reviewAttemptId": "review-one"}]
                if path.endswith("/audit"):
                    return [{"action": "review.reported", "targetId": "review-one",
                             "detailJson": '{"Outcome":"Pass"}'}]
                return {"state": "5-human-review"}

            with mock.patch.object(MODULE, "api", side_effect=respond), \
                 mock.patch.object(MODULE.subprocess, "run", return_value=types.SimpleNamespace(stdout="def\trefs/heads/main\n")):
                MODULE.verify(args, "management", monotonic=lambda: 0, pause=lambda _: None)
            self.assertTrue(any(path.endswith("/history") for path in observed))

    def test_verify_rejects_unpublished_card_with_injected_clock(self):
        with tempfile.TemporaryDirectory() as temporary:
            card = Path(temporary) / "card.json"
            card.write_text(json.dumps({"taskId": "task-one", "taskKey": "SCN-1"}))
            args = types.SimpleNamespace(server_url="http://127.0.0.1:5071",
                                         repository_url="git://127.0.0.1/scenario.git",
                                         card_file=card, timeout=1)
            ticks = iter((0, 0, 2))
            with mock.patch.object(MODULE, "api", return_value={"state": "4-auto-review"}):
                with self.assertRaisesRegex(RuntimeError, "did not reach integration"):
                    MODULE.verify(args, "management", monotonic=lambda: next(ticks), pause=lambda _: None)


if __name__ == "__main__":
    unittest.main()
