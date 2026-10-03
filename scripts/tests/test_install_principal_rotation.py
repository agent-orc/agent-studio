import importlib.util
import contextlib
import io
import json
import pathlib
import stat
import sys
import tempfile
import unittest
import urllib.error
from unittest import mock


SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "install-principal-rotation.py"
SPEC = importlib.util.spec_from_file_location("principal_rotation_installer", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PrincipalRotationInstallerTests(unittest.TestCase):
    def test_group_readable_existing_file_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "runner.token"
            path.write_text("old-bearer\n", encoding="ascii")
            path.chmod(0o640)
            with self.assertRaises(ValueError):
                MODULE.install(str(path), "new-bearer")
            self.assertEqual("old-bearer\n", path.read_text(encoding="ascii"))

    def test_resume_after_backup_before_replacement_uses_protected_stage(self):
        self._exercise_interrupted_main("replace")

    def test_resume_after_delivery_before_ack_uses_installed_bearer(self):
        self._exercise_interrupted_main("ack")

    def test_resume_after_ack_before_old_bearer_check_finishes(self):
        self._exercise_interrupted_main("after_ack")

    def _exercise_interrupted_main(self, interruption):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "runner.token"
            path.write_text("old-bearer\n", encoding="ascii")
            path.chmod(0o600)
            bearer = "ats_0123456789abcdef0123456789abcdef." + "a" * 64
            receipt = {
                "operationId": "rotation-1", "credentialGeneration": "0123456789abcdef0123456789abcdef",
                "state": "issued", "consumers": [{"consumerId": "runner-a", "requiredScope": "tasks:read"}],
            }
            argv = ["installer", "--server", "https://task-server.example", "--operation-id", "rotation-1",
                    "--consumer-id", "runner-a", "--token-file", str(path)]
            issued = json.dumps({"rotation": receipt, "credential": bearer})
            original_install = MODULE.install
            calls = []
            failed = [False]

            def install(target, value):
                if interruption == "replace" and target == str(path) and value == bearer and not failed[0]:
                    failed[0] = True
                    raise OSError("simulated process interruption")
                original_install(target, value)

            def call(server, route, credential, consumer, method="POST", body=None):
                self.assertEqual(bearer, credential)
                calls.append(route)
                if method == "GET":
                    return receipt
                if route.endswith("/ack"):
                    if interruption == "ack" and not failed[0]:
                        failed[0] = True
                        raise OSError("simulated process interruption")
                    return {"state": "retired", "acknowledgedConsumers": ["runner-a"]}
                return {"state": "delivered"}

            def check_old(server, operation, previous, consumer):
                if interruption == "after_ack" and not failed[0]:
                    failed[0] = True
                    raise OSError("simulated process interruption")
                return True

            with mock.patch.object(sys, "argv", argv), mock.patch.object(sys, "stdin", io.StringIO(issued)), \
                    mock.patch.object(MODULE, "install", side_effect=install), mock.patch.object(MODULE, "call", side_effect=call), \
                    mock.patch.object(MODULE, "old_bearer_rejected", side_effect=check_old):
                with self.assertRaises(OSError):
                    MODULE.main()
                self.assertTrue(failed[0])
                if interruption == "replace":
                    pending = pathlib.Path(str(path) + ".rotation-rotation-1.pending")
                    self.assertEqual(bearer, MODULE.read_installed(str(pending))[0])
                    self.assertEqual(0o600, stat.S_IMODE(pending.stat().st_mode))
                    self.assertEqual("old-bearer", MODULE.read_installed(str(path))[0])
                with mock.patch.object(sys, "argv", argv + ["--resume"]):
                    MODULE.main()

            self.assertEqual(bearer, MODULE.read_installed(str(path))[0])
            self.assertFalse(pathlib.Path(str(path) + ".rotation-rotation-1.previous").exists())
            self.assertFalse(pathlib.Path(str(path) + ".rotation-rotation-1.pending").exists())
            self.assertIn("/api/v1/principal-rotations/rotation-1/ack", calls)

    def test_atomic_install_preserves_restricted_mode_and_replaces_old_bearer(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "runner.token"
            path.write_text("old-bearer\n", encoding="ascii")
            path.chmod(0o600)

            MODULE.install(str(path), "new-bearer")

            self.assertEqual("new-bearer", MODULE.read_installed(str(path))[0])
            self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
            self.assertEqual(["runner.token"], [item.name for item in path.parent.iterdir()])

    def test_symlink_target_is_rejected_before_staging(self):
        with tempfile.TemporaryDirectory() as directory:
            real = pathlib.Path(directory) / "real.token"
            real.write_text("old-bearer\n", encoding="ascii")
            link = pathlib.Path(directory) / "runner.token"
            link.symlink_to(real)

            with self.assertRaises((ValueError, OSError)):
                MODULE.install(str(link), "new-bearer")

            self.assertEqual("old-bearer\n", real.read_text(encoding="ascii"))

    def test_recovered_or_retired_receipt_cannot_replace_host_secret(self):
        issued = {"credentialGeneration": "generation-a", "consumers": [{"consumerId": "edge-a"}]}
        for state in ("retired", "recovery-required", "superseded-in-recovery", "revoked"):
            with self.assertRaises(RuntimeError):
                MODULE.require_installable(issued,
                    {"credentialGeneration": "generation-a", "state": state})
        with self.assertRaises(RuntimeError):
            MODULE.require_installable(issued,
                {"credentialGeneration": "generation-b", "state": "issued"})

    def test_shared_receipt_requires_the_selected_consumer_generation(self):
        receipt = {"state": "issued", "consumers": [{"consumerId": "edge-a"}, {"consumerId": "edge-b"}],
                   "consumerCredentialGenerations": {"edge-a": "generation-a", "edge-b": "generation-b"}}
        MODULE.require_installable(receipt, receipt, "edge-b")
        changed = dict(receipt, consumerCredentialGenerations={"edge-a": "generation-a", "edge-b": "other"})
        with self.assertRaises(RuntimeError):
            MODULE.require_installable(receipt, changed, "edge-b")
        with self.assertRaises(ValueError):
            MODULE.require_consumer_bearer(receipt, "edge-b", "ats_generation-a." + "a" * 64)

    def test_ack_retries_only_missing_consumer_proof_with_a_fake_clock(self):
        ticks = [0]
        attempts = [0]

        def send_ack():
            attempts[0] += 1
            if attempts[0] < 3:
                raise MODULE.TaskServerHttpError(409, "rotation-scope-proof-required")
            return {"state": "retired"}

        def sleep(seconds):
            ticks[0] += seconds

        self.assertEqual({"state": "retired"}, MODULE.wait_for_ack(
            send_ack, 5, now=lambda: ticks[0], sleep=sleep))
        self.assertEqual(3, attempts[0])
        self.assertEqual(4, ticks[0])

        with self.assertRaises(MODULE.TaskServerHttpError):
            MODULE.wait_for_ack(
                lambda: (_ for _ in ()).throw(MODULE.TaskServerHttpError(409, "rotation-recovery-required")),
                5, now=lambda: ticks[0], sleep=sleep)

        attempts[0] = 0
        ticks[0] = 0

        def still_missing():
            attempts[0] += 1
            raise MODULE.TaskServerHttpError(409, "rotation-scope-proof-required")

        with self.assertRaises(MODULE.TaskServerHttpError):
            MODULE.wait_for_ack(still_missing, 3, now=lambda: ticks[0], sleep=sleep)
        self.assertEqual(3, attempts[0])
        self.assertEqual(3, ticks[0])

    def test_old_generation_check_accepts_only_unauthorized(self):
        class Opener:
            def __init__(self, status):
                self.status = status

            def open(self, request, timeout):
                if self.status == 200:
                    return contextlib.nullcontext()
                raise urllib.error.HTTPError(request.full_url, self.status, "fixture", {}, None)

        for status, expected in ((401, True), (403, False), (409, False), (200, False)):
            self.assertEqual(expected, MODULE.old_bearer_rejected(
                "https://task-server.example", "operation", "redacted-old", "runner-a",
                opener=Opener(status)))


if __name__ == "__main__":
    unittest.main()
