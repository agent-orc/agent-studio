"""Fake-host checks for the repository-owned networked adapter."""

import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
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
    def test_example_requires_running_compose_project_labels(self):
        example = json.loads((ROOT / "manifest.example.json").read_text())
        hosts = {host["role"]: host for host in example["hosts"]}
        self.assertEqual(hosts["server"]["composeProject"], "SET_RUNNING_SERVER_COMPOSE_PROJECT")
        self.assertEqual(hosts["engine"]["engineComposeProject"], "SET_RUNNING_ENGINE_COMPOSE_PROJECT")

    @staticmethod
    def release_archive(path):
        with tarfile.open(path, "w:gz") as stream:
            payload = b"release contents"
            info = tarfile.TarInfo("agent-host")
            info.size = len(payload)
            stream.addfile(info, io.BytesIO(payload))
        return adapter.sha(path)

    def test_interrupted_release_extraction_can_retry(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            archive = root / "release.tar.gz"
            checksum = self.release_archive(archive)
            releases = root / "releases"
            original = tarfile.TarFile.extractall

            def interrupted(stream, destination, **kwargs):
                (Path(destination) / "partial").write_text("unfinished")
                raise OSError("interrupted extraction")

            with patch.object(tarfile.TarFile, "extractall", interrupted):
                with self.assertRaisesRegex(OSError, "interrupted extraction"):
                    remote.install_archive(archive, checksum, releases, "a" * 40)
            self.assertFalse((releases / checksum).exists())
            with patch.object(tarfile.TarFile, "extractall", original):
                installed = remote.install_archive(archive, checksum, releases, "a" * 40)
            self.assertEqual((installed / "agent-host").read_bytes(), b"release contents")
            self.assertEqual((installed / "COMMIT").read_text().strip(), "a" * 40)

            # Recover a partial directory left by the previous direct extraction flow.
            (installed / "COMMIT").unlink()
            (installed / "agent-host").unlink()
            (installed / "partial").write_text("unfinished")
            restored = remote.install_archive(archive, checksum, releases, "a" * 40)
            self.assertEqual((restored / "agent-host").read_bytes(), b"release contents")
            self.assertFalse((restored / "partial").exists())

    def test_interrupted_coding_extraction_can_retry(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            archive = root / "release.tar.gz"
            checksum = self.release_archive(archive)
            incoming = root / "incoming"
            incoming.mkdir()
            request = {"action": "coding_install", "host": {"runnerIncoming": str(incoming)},
                       "settings": {}, "fields": [str(archive), "original", checksum, "a" * 40]}
            original = tarfile.TarFile.extractall

            def interrupted(stream, destination, **kwargs):
                (Path(destination) / "partial").write_text("unfinished")
                raise OSError("interrupted extraction")

            with patch.object(tarfile.TarFile, "extractall", interrupted):
                with self.assertRaisesRegex(OSError, "interrupted extraction"):
                    remote.main(request)
            with patch.object(tarfile.TarFile, "extractall", original), \
                 patch.object(remote, "run", return_value="") as command:
                self.assertTrue(remote.main(request)["installed"])
                command.assert_called_once()
            self.assertEqual((incoming / "agent-host").read_bytes(), b"release contents")
            self.assertFalse((incoming / "partial").exists())

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
            # The installed stack predates the new digest and gate variables.
            env.write_text("LEGACY_SETTING=1\n")
            engine.write_text("ORCHESTRATOR_ENGINE_IMAGE_REF=repo/engine@sha256:" + "a" * 64 + "\n")
            host = {"role": "server", "envFile": str(env), "engineEnvFile": str(engine),
                    "composeDir": str(root), "engineComposeDir": str(root), "healthUrl": "http://fake/health",
                    "gateSshHost": "new-gate", "composeProject": "agent-studio-website"}
            settings = {"images": {"api": "repo/api", "web": "repo/web"}}
            request = {"host": host, "settings": settings, "fields": [], "action": "snapshot"}
            old = {"api": "repo/api:old-tag", "frontend": "repo/web:old-tag"}
            ids = {"api": "sha256:" + "c" * 64, "frontend": "sha256:" + "d" * 64}
            def snapshot_run(argv, cwd=None):
                if argv[:2] == ["docker", "ps"]:
                    self.assertIn("label=com.docker.compose.project=agent-studio-website", argv)
                    return "api" if "label=com.docker.compose.service=api" in argv else "frontend"
                if "Config.Env" in argv[-2]:
                    return "LEGACY_SETTING=1"
                if argv[:3] == ["docker", "image", "inspect"]:
                    if argv[-2] == "{{.Id}}":
                        return ids["api"] if argv[-1] == old["api"] else ids["frontend"]
                    repository = "repo/api" if argv[-1] == ids["api"] else "repo/web"
                    if argv[-1] in old.values():
                        repository = "repo/api" if argv[-1] == old["api"] else "repo/web"
                    return json.dumps([repository + "@sha256:" + "a" * 64])
                if ".Config.Image" in argv[-2]:
                    return old[argv[-1]]
                if ".Image" in argv[-2]:
                    return ids[argv[-1]]
                raise AssertionError(argv)
            with patch.object(remote, "run", side_effect=snapshot_run):
                before = remote.main(request)["value"]
            self.assertEqual(before["apiImage"], "repo/api:old-tag")
            self.assertEqual(before["apiDigest"], "a" * 64)
            self.assertEqual(before["apiImageId"], ids["api"])
            self.assertIsNone(before["gateSshHost"])
            self.assertIsNone(before["workerPath"])
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
            self.assertNotIn("STUDIO_GATE_SSH_HOST", remote.env_read(env))
            self.assertNotIn("STUDIO_GATE_WORKER_PATH", remote.env_read(env))

    def test_engine_switch_and_rollback_restore_previous_image(self):
        with tempfile.TemporaryDirectory() as folder:
            env = Path(folder) / "engine.env"
            old = "repo/engine@sha256:" + "a" * 64
            new = "repo/engine@sha256:" + "b" * 64
            env.write_text("ORCHESTRATOR_ENGINE_IMAGE_REF=" + old + "\n")
            host = {"role": "engine", "engineEnvFile": str(env), "engineComposeDir": folder,
                    "engineComposeProject": "agent-studio-monolith-engine"}
            request = {"action": "snapshot", "host": host, "settings": {}, "fields": []}
            with patch.object(remote, "run", side_effect=lambda argv, cwd=None:
                              "engine-container" if argv[:2] == ["docker", "ps"] else old):
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
                                                        "sha256:" + "c" * 64,
                                                        json.dumps(["other/api@sha256:" + "a" * 64])]):
            with self.assertRaisesRegex(RuntimeError, "needs one digest for repo/api"):
                remote.running_image("api-container", "repo/api")

    def test_tagged_server_snapshot_resolves_the_original_tag_before_the_image_id(self):
        image_id = "sha256:" + "c" * 64
        digest = "a" * 64

        def observed(argv, cwd=None):
            if argv[-2] == "{{.Config.Image}}":
                return "repo/api:prior"
            if argv[-2] in ("{{.Image}}", "{{.Id}}"):
                return image_id
            if argv[-2] == "{{json .RepoDigests}}":
                self.assertEqual(argv[-1], "repo/api:prior")
                return json.dumps([f"repo/api@sha256:{digest}"])
            raise AssertionError(argv)

        with patch.object(remote, "run", side_effect=observed):
            self.assertEqual(remote.running_image("api", "repo/api")["digest"], digest)

    def test_snapshot_refuses_missing_or_ambiguous_running_compose_service(self):
        for containers in ("", "first\nsecond"):
            with self.subTest(containers=containers), patch.object(remote, "run", return_value=containers) as command:
                with self.assertRaisesRegex(RuntimeError, "expected one running api container"):
                    remote.running_compose_service("agent-studio-website", "api")
                self.assertEqual(command.call_args.args[0][:2], ["docker", "ps"])

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
