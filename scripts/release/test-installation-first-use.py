#!/usr/bin/env python3
"""Focused contract for the owner session used by the setup join probe."""

import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock


SCRIPT = Path(__file__).with_name("installation-first-use.py")
SPEC = importlib.util.spec_from_file_location("installation_first_use", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class InstallationFirstUseTests(unittest.TestCase):
    def test_bootstrap_saves_protected_owner_session_for_join_issuance(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "studio.token").write_text("management")
            (root / "owner.code").write_text("bootstrap")
            session_file = root / "owner.session"
            paths = []

            def reply(_url, _bearer, path, _payload, session=""):
                paths.append((path, session))
                if path.endswith("/bootstrap"):
                    return {"sessionToken": "owner-session"}
                if path == "/api/v1/workspaces":
                    return {"workspaceId": "workspace-one"}
                return {"projectId": "scenario-project"}

            arguments = [str(SCRIPT), "--server-url", "http://127.0.0.1:5071",
                         "--studio-token-file", str(root / "studio.token"),
                         "--owner-code-file", str(root / "owner.code"),
                         "--repository-url", "git://127.0.0.1/scenario.git",
                         "--owner-session-file", str(session_file)]
            with mock.patch.object(sys, "argv", arguments), mock.patch.object(MODULE, "post", side_effect=reply):
                self.assertEqual(0, MODULE.main())
            self.assertEqual("owner-session\n", session_file.read_text())
            if os.name != "nt":
                self.assertEqual(0o600, session_file.stat().st_mode & 0o777)
            self.assertEqual([
                ("/api/v1/studio/auth/bootstrap", ""),
                ("/api/v1/workspaces", "owner-session"),
                ("/api/v1/projects/registrations", "owner-session"),
            ], paths)


if __name__ == "__main__":
    unittest.main()
