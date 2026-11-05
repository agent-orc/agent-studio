"""Fake-host checks for the repository-owned networked adapter."""

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / f"{name}.py")
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


adapter = module("host_adapter")
remote = module("remote_host")


class HostAdapterTests(unittest.TestCase):
    def test_example_manifest_uses_repository_adapter_for_every_host_action(self):
        example = json.loads((ROOT / "manifest.example.json").read_text())
        commands = example["commands"]
        for name, value in commands.items():
            if name in ("preflight", "plan"):
                continue
            entries = value.values() if isinstance(value, dict) else [value]
            for entry in entries:
                self.assertEqual(entry[:4], ["python3", "{adapter}", "--config", "{manifest}"])
                self.assertNotIn("/path/to/local-adapter", entry)

    def test_ssh_sends_repository_remote_code_and_private_config(self):
        config = {"hosts": [], "images": {"api": "repo/api"}}
        host = {"id": "fake", "role": "server", "target": "fake-host"}
        with patch.object(adapter.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, '{"drained":true}', '')) as called:
            self.assertEqual(adapter.remote(config, host, "drain", []), {"drained": True})
        argv = called.call_args.args[0]
        self.assertEqual(argv[0], "ssh")
        self.assertEqual(argv[-2], "fake-host")
        self.assertIn("remote_host.py", (ROOT / "remote_host.py").as_posix())
        request = json.loads(called.call_args.kwargs["input"])
        self.assertEqual(request["host"], host)
        self.assertEqual(request["settings"], config)

    def test_fake_ssh_executes_remote_adapter_without_a_host(self):
        with tempfile.TemporaryDirectory() as folder:
            fake = Path(folder) / "ssh"
            fake.write_text('#!/bin/bash\ncommand="${@: -1}"\nexec /bin/sh -c "$command"\n')
            fake.chmod(0o755)
            host = {"id": "fake", "role": "server", "target": "fake-host",
                    "admissionDrainCommand": ["/bin/true"]}
            with patch.dict(os.environ, {"PATH": folder + os.pathsep + os.environ["PATH"]}):
                self.assertEqual(adapter.remote({"hosts": [host]}, host, "drain", []), {"drained": True})

    def test_immutable_release_can_be_reused_after_blocked_preflight(self):
        commit = "a" * 40
        with tempfile.TemporaryDirectory() as folder:
            release = Path(folder) / commit / "agent_host.tar.gz"
            release.parent.mkdir()
            release.write_bytes(b"fake release")
            checksum = adapter.sha(release)
            (release.parent / "agent_host.sha256").write_text(checksum + "\n")
            result = adapter.build({"artifactDirectory": folder}, "build-agent-host", commit)
            self.assertEqual(result, {"commit": commit, "ref": str(release), "sha256": checksum})

    def test_transfer_refuses_a_changed_release_archive(self):
        with tempfile.TemporaryDirectory() as folder:
            archive = Path(folder) / "release.tar.gz"
            archive.write_bytes(b"changed")
            with patch.object(adapter, "run") as transfer:
                with self.assertRaisesRegex(adapter.AdapterError, "digest changed"):
                    adapter.copy_release({"target": "fake-host"}, str(archive), "a" * 64,
                                         "/tmp/fake.tar.gz")
            transfer.assert_not_called()

    def test_server_switch_and_rollback_restore_snapshot_fields(self):
        digest = "b" * 64
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            env = root / "server.env"
            engine = root / "engine.env"
            env.write_text("STUDIO_API_DIGEST=" + "a" * 64 + "\nSTUDIO_WEB_DIGEST=" + "a" * 64 + "\nSTUDIO_GATE_WORKER_PATH=/old/worker\n")
            engine.write_text("ORCHESTRATOR_ENGINE_IMAGE_REF=repo/engine@sha256:" + "a" * 64 + "\n")
            host = {"role": "server", "envFile": str(env), "engineEnvFile": str(engine),
                    "composeDir": str(root), "engineComposeDir": str(root), "healthUrl": "http://fake/health",
                    "gateSshHost": "new-gate"}
            settings = {"images": {"api": "repo/api", "web": "repo/web"}}
            request = {"host": host, "settings": settings, "fields": [], "action": "snapshot"}
            old = {"api": "repo/api:old-tag", "frontend": "repo/web:old-tag"}
            ids = {"api": "sha256:" + "c" * 64, "frontend": "sha256:" + "d" * 64}
            def snapshot_run(argv, cwd=None):
                if "Config.Env" in argv[-2]:
                    return "RemoteGate__WorkerPath=/old/worker\nRemoteGate__SshHost=old-gate"
                if argv[:3] == ["docker", "image", "inspect"]:
                    repository = "repo/api" if argv[-1] == ids["api"] else "repo/web"
                    return json.dumps([repository + "@sha256:" + "a" * 64])
                if ".Config.Image" in argv[-2]:
                    return old[argv[-1]]
                if ".Image" in argv[-2]:
                    return ids[argv[-1]]
                raise AssertionError(argv)
            with patch.object(remote, "run", side_effect=snapshot_run), \
                 patch.object(remote, "compose_ps", side_effect=lambda _dir, _env, service: service):
                before = remote.main(request)["value"]
            self.assertEqual(before["apiImage"], "repo/api:old-tag")
            self.assertEqual(before["apiDigest"], "a" * 64)
            self.assertEqual(before["apiImageId"], ids["api"])
            def fake_run(argv, cwd=None):
                if argv[:2] == ["docker", "inspect"] and ".Image" in argv[-2]:
                    return ids[argv[-1]]
                return ""
            with patch.object(remote, "run", side_effect=fake_run) as commands, \
                 patch.object(remote, "compose_ps", side_effect=lambda _dir, _env, service: service):
                request.update(action="server_switch", fields=[f"repo/api@sha256:{digest}",
                                                               f"repo/web@sha256:{digest}", "/new/worker"])
                self.assertTrue(remote.main(request)["switched"])
                self.assertEqual(remote.env_read(env)["STUDIO_GATE_WORKER_PATH"], "/new/worker")
                request.update(action="rollback_server", fields=[json.dumps(before)])
                self.assertTrue(remote.main(request)["restored"])
            pulls = [call.args[0][-1] for call in commands.call_args_list
                     if call.args[0][:2] == ["docker", "pull"]]
            self.assertIn("repo/api@sha256:" + "a" * 64, pulls)
            self.assertIn("repo/web@sha256:" + "a" * 64, pulls)
            self.assertEqual(remote.env_read(env)["STUDIO_API_DIGEST"], "a" * 64)
            self.assertEqual(remote.env_read(env)["STUDIO_GATE_SSH_HOST"], "old-gate")
            self.assertEqual(remote.env_read(env)["STUDIO_GATE_WORKER_PATH"], "/old/worker")

    def test_engine_switch_and_rollback_restore_previous_image(self):
        with tempfile.TemporaryDirectory() as folder:
            env = Path(folder) / "engine.env"
            old = "repo/engine@sha256:" + "a" * 64
            new = "repo/engine@sha256:" + "b" * 64
            env.write_text("ORCHESTRATOR_ENGINE_IMAGE_REF=" + old + "\n")
            host = {"role": "engine", "engineEnvFile": str(env), "engineComposeDir": folder}
            request = {"action": "snapshot", "host": host, "settings": {}, "fields": []}
            with patch.object(remote, "run", side_effect=lambda argv, cwd=None:
                              "healthy" if "State.Health.Status" in argv[-2] else old), \
                 patch.object(remote, "compose_ps", return_value="engine-container"):
                previous = remote.main(request)["value"]
            with patch.object(remote, "run", side_effect=lambda argv, cwd=None:
                              "healthy" if "State.Health.Status" in argv[-2] else old), \
                 patch.object(remote, "compose_ps", return_value="engine-container"):
                request.update(action="engine_switch", fields=[new])
                remote.main(request)
                self.assertEqual(remote.env_read(env)["ORCHESTRATOR_ENGINE_IMAGE_REF"], new)
                request.update(action="rollback_engine", fields=[json.dumps(previous)])
                self.assertTrue(remote.main(request)["restored"])
            self.assertEqual(remote.env_read(env)["ORCHESTRATOR_ENGINE_IMAGE_REF"], old)

    def test_tagged_server_snapshot_refuses_unresolved_running_digest(self):
        with patch.object(remote, "run", side_effect=["repo/api:old-tag", "sha256:" + "c" * 64,
                                                        json.dumps(["other/api@sha256:" + "a" * 64])]):
            with self.assertRaisesRegex(RuntimeError, "needs one digest for repo/api"):
                remote.running_image("api-container", "repo/api")

    def test_review_recreate_refuses_busy_host_before_changing_image(self):
        with tempfile.TemporaryDirectory() as folder:
            env = Path(folder) / "review.env"
            env.write_text("REVIEW_IMAGE_REF=old\n")
            host = {"role": "review", "reviewDrainCommand": ["fake-drain"],
                    "reviewIdleCommand": ["fake-idle"], "reviewEnvFile": str(env)}
            request = {"action": "review_recreate", "host": host, "settings": {},
                       "fields": ["repo/review@sha256:" + "b" * 64, "plan", "a" * 64]}
            with patch.object(remote, "run", side_effect=["", '{"idle":false}']):
                with self.assertRaisesRegex(RuntimeError, "busy"):
                    remote.main(request)
            self.assertEqual(remote.env_read(env)["REVIEW_IMAGE_REF"], "old")


if __name__ == "__main__":
    unittest.main()
