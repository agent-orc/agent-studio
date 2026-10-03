import importlib.util
import contextlib
import pathlib
import stat
import tempfile
import unittest
import urllib.error


SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "install-principal-rotation.py"
SPEC = importlib.util.spec_from_file_location("principal_rotation_installer", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PrincipalRotationInstallerTests(unittest.TestCase):
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
        issued = {"credentialGeneration": "generation-a"}
        for state in ("retired", "recovery-required", "superseded-in-recovery", "revoked"):
            with self.assertRaises(RuntimeError):
                MODULE.require_installable(issued,
                    {"credentialGeneration": "generation-a", "state": state})
        with self.assertRaises(RuntimeError):
            MODULE.require_installable(issued,
                {"credentialGeneration": "generation-b", "state": "issued"})

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
