import os
import stat
import tempfile
import unittest
import base64
import hashlib
import json
import subprocess
import sys
import io
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from deploy.host_secret_transport import (
    FileSecretAdapter,
    EnvironmentFileAdapter,
    NativeCliStoreAdapter,
    DockerSecretMountAdapter,
    SecretPolicyError,
    DeliveryEnvelopeStore,
    WireGuardRotation,
    SshKeyRotation,
    PinnedSshAdministration,
    HostWireGuardPeer,
    LinuxWireGuardGateway,
    generate_wireguard_keypair,
    AuthenticatedTaskServerProbe,
    require_current_host_authority,
    HostAuthorityBinding,
    HttpsHostAuthoritySource,
    main,
)


def current_authority(kind):
    class Source:
        def current(self, host_id, credential_kind):
            return ("instance-a", "generation-a", False)

    return HostAuthorityBinding(Source(), "host-a", kind,
                                "instance-a", "generation-a")


class HostSecretTransportTests(unittest.TestCase):
    def test_atomic_replacement_and_actual_permissions(self):
        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "native"
            parent.mkdir(mode=0o700)
            target = parent / "auth.json"
            adapter = FileSecretAdapter(target, os.getuid(), os.getgid(), 0o700, 0o600)
            first = adapter.install(b'{"fixture":"old"}', "op-1", "g1")
            old = target.open("rb")
            second = adapter.install(b'{"fixture":"new"}', "op-2", "g2")
            self.assertEqual(old.read(), b'{"fixture":"old"}')
            self.assertEqual(target.read_bytes(), b'{"fixture":"new"}')
            self.assertNotEqual(first.generation, second.generation)
            self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o600)
            self.assertNotIn("fixture", repr(second))
            old.close()

    def test_insecure_existing_file_and_symlink_are_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "native"
            parent.mkdir(mode=0o700)
            target = parent / "auth.json"
            target.write_bytes(b"old")
            target.chmod(0o644)
            adapter = FileSecretAdapter(target, os.getuid(), os.getgid(), 0o700, 0o600)
            with self.assertRaises(SecretPolicyError):
                adapter.install(b"new", "op-1", "g2")
            target.unlink()
            target.symlink_to(Path(root) / "elsewhere")
            with self.assertRaises(SecretPolicyError):
                adapter.install(b"new", "op-1", "g2")

    def test_native_and_environment_formats_are_checked_before_replacement(self):
        with tempfile.TemporaryDirectory() as root:
            native = Path(root) / "native"
            native.mkdir(mode=0o700)
            auth = native / "auth.json"
            adapter = NativeCliStoreAdapter(auth, os.getuid(), os.getgid())
            adapter.install(b'{"fixture":"old"}', "op-1", "g1")
            with self.assertRaises(SecretPolicyError):
                adapter.install(b"bad json", "op-2", "g2")
            self.assertEqual(auth.read_bytes(), b'{"fixture":"old"}')
            envdir = Path(root) / "environment"
            envdir.mkdir(mode=0o750)
            envfile = envdir / "provider-auth.env"
            env = EnvironmentFileAdapter(envfile, os.getuid(), os.getgid())
            env.install(b"PROVIDER_TOKEN=fixture\n", "op-1", "g1")
            self.assertEqual(stat.S_IMODE(envfile.stat().st_mode), 0o640)
            with self.assertRaises(SecretPolicyError):
                env.install(b"PROVIDER_TOKEN=a\nPROVIDER_TOKEN=b\n", "op-2", "g2")
            self.assertEqual(envfile.read_bytes(), b"PROVIDER_TOKEN=fixture\n")

    def test_service_secret_requires_read_only_mount(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "secret"
            path.write_bytes(b"fixture")
            path.chmod(0o600)
            with self.assertRaises(SecretPolicyError):
                DockerSecretMountAdapter(path, os.getuid(), os.getgid()).read()

    def test_envelope_is_bound_to_host_generation_expiry_and_one_use(self):
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
        from cryptography.hazmat.primitives import serialization

        with tempfile.TemporaryDirectory() as root:
            key = X25519PrivateKey.generate()
            public = key.public_key().public_bytes(
                serialization.Encoding.Raw, serialization.PublicFormat.Raw)
            now = [1000]
            store = DeliveryEnvelopeStore(Path(root), clock=lambda: now[0])
            ref = store.issue("host-a", "op-7", "g2", 60, b"fixture", public)
            with self.assertRaises(SecretPolicyError):
                store.consume(ref, "host-b", "op-7", "g2", key)
            with self.assertRaises(SecretPolicyError):
                store.consume(ref, "host-a", "op-7", "g1", key)
            self.assertEqual(store.consume(ref, "host-a", "op-7", "g2", key), b"fixture")
            with self.assertRaises(SecretPolicyError):
                store.consume(ref, "host-a", "op-7", "g2", key)
            ref = store.issue("host-a", "op-8", "g3", 60, b"fixture", public)
            now[0] = 1061
            with self.assertRaises(SecretPolicyError):
                store.consume(ref, "host-a", "op-8", "g3", key)

    def test_cli_envelope_delivers_into_native_store_without_secret_in_receipt(self):
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
        from cryptography.hazmat.primitives import serialization

        with tempfile.TemporaryDirectory() as root:
            base = Path(root)
            envelope = base / "envelopes"
            envelope.mkdir(mode=0o700)
            custody = base / "custody"
            custody.mkdir(mode=0o700)
            target = custody / "auth.json"
            key = X25519PrivateKey.generate()
            public = custody / "host.pub"
            public.write_bytes(key.public_key().public_bytes(
                serialization.Encoding.Raw, serialization.PublicFormat.Raw))
            private = custody / "host.key"
            private.write_bytes(key.private_bytes(
                serialization.Encoding.Raw, serialization.PrivateFormat.Raw,
                serialization.NoEncryption()))
            private.chmod(0o600)
            fixture = b'{"fixture":"redacted"}'
            common = ["--directory", str(envelope), "--host-id", "host-a",
                      "--operation-ref", "operation-1", "--generation", "generation-2"]
            issued = subprocess.run(
                [sys.executable, "-m", "deploy.host_secret_transport", "issue-envelope",
                 *common, "--ttl-seconds", "60", "--host-public-key-file", str(public)],
                input=fixture, capture_output=True, check=True)
            reference = json.loads(issued.stdout)["envelopeRef"]
            self.assertNotIn(b"redacted", issued.stdout)
            self.assertNotIn(b"redacted", (envelope / (reference + ".json")).read_bytes())
            class Source:
                def current(self, host_id, credential_kind):
                    return ("instance-a", "generation-2", False)

            out = io.StringIO()
            with patch("deploy.host_secret_transport.HttpsHostAuthoritySource",
                       return_value=Source()), redirect_stdout(out):
                code = main(["consume-envelope", *common, "--envelope-ref", reference,
                             "--host-private-key-file", str(private), "--profile", "native",
                             "--target", str(target), "--uid", str(os.getuid()),
                             "--gid", str(os.getgid()), "--instance-id", "instance-a",
                             "--credential-kind", "provider_api_key",
                             "--authority-url", "https://issuer.example/authority",
                             "--authority-token-file", str(private)])
            self.assertEqual(code, 0)
            self.assertEqual(target.read_bytes(), fixture)
            self.assertNotIn("redacted", out.getvalue())
            self.assertFalse((envelope / (reference + ".json")).exists())

    def test_restored_host_install_checks_live_issuer_before_replacing_file(self):
        class Source:
            record = ("instance-current", "generation-current", False)
            calls = []

            def current(self, host_id, credential_kind):
                self.calls.append((host_id, credential_kind))
                return self.record

        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "native"
            parent.mkdir(mode=0o700)
            target = parent / "auth.json"
            source = Source()
            binding = HostAuthorityBinding(source, "host-a", "provider_api_key",
                                           "instance-restored", "generation-old")
            with self.assertRaisesRegex(SecretPolicyError, "current credential authority"):
                binding.install(NativeCliStoreAdapter(target, os.getuid(), os.getgid()),
                                b'{"fixture":"redacted"}', "op-1", "generation-old")
            self.assertFalse(target.exists())
            self.assertEqual(source.calls, [("host-a", "provider_api_key")])
            source.record = ("instance-restored", "generation-old", True)
            with self.assertRaises(SecretPolicyError):
                binding.install(NativeCliStoreAdapter(target, os.getuid(), os.getgid()),
                                b'{"fixture":"redacted"}', "op-1", "generation-old")
            self.assertFalse(target.exists())

    def test_restored_host_route_rejects_revoked_generation_before_staging(self):
        class Source:
            def current(self, host_id, credential_kind):
                return ("instance-a", "generation-a", True)

        class Gateway:
            def peers(self):
                raise AssertionError("route inventory must not run")

        class Peer:
            def stage(self, peer):
                raise AssertionError("candidate must not be staged")

        authority = HostAuthorityBinding(Source(), "host-a", "wireguard_peer",
                                         "instance-a", "generation-a")
        with self.assertRaises(SecretPolicyError):
            WireGuardRotation(Gateway(), Peer(), authority=authority).rotate(
                "op-1", "old", "candidate", ["10.60.0.3/32"])

    def test_https_authority_reads_current_issuer_and_fails_closed(self):
        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "issuer"
            parent.mkdir(mode=0o700)
            token_file = parent / "token"
            token_file.write_text("fixture-issuer-token\n")
            token_file.chmod(0o600)
            calls = []

            class Response(io.BytesIO):
                status = 200

            def open_url(request, timeout):
                calls.append((request.full_url, request.get_header("Authorization"), timeout))
                return Response(json.dumps({"hostId": "host-a", "credentialKind": "wireguard_peer",
                                            "instanceId": "instance-a", "generation": "g2",
                                            "revoked": False}).encode())

            source = HttpsHostAuthoritySource(
                "https://issuer.example/current", token_file, open_url=open_url)
            self.assertEqual(source.current("host-a", "wireguard_peer"),
                             ("instance-a", "g2", False))
            self.assertIn("hostId=host-a", calls[0][0])
            self.assertEqual(calls[0][1], "Bearer fixture-issuer-token")
            self.assertEqual(calls[0][2], 5)
            source.open_url = lambda request, timeout: (_ for _ in ()).throw(OSError("offline"))
            with self.assertRaises(SecretPolicyError):
                require_current_host_authority(source, "host-a", "wireguard_peer",
                                               "instance-a", "g2")

    def test_cli_refuses_revoked_host_before_consuming_envelope(self):
        class Source:
            def current(self, host_id, credential_kind):
                return ("instance-a", "g2", True)

        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "native"
            parent.mkdir(mode=0o700)
            envelope_dir = Path(root) / "envelopes"
            envelope_dir.mkdir(mode=0o700)
            envelope = envelope_dir / "env_fixture123.json"
            envelope.write_text('{"fixture":"ciphertext"}')
            private = parent / "host.key"
            private.write_bytes(b"x" * 32)
            private.chmod(0o600)
            target = parent / "auth.json"
            error = io.StringIO()
            with patch("deploy.host_secret_transport.HttpsHostAuthoritySource",
                       return_value=Source()), redirect_stderr(error):
                code = main(["consume-envelope", "--profile", "native",
                             "--target", str(target), "--uid", str(os.getuid()),
                             "--gid", str(os.getgid()), "--operation-ref", "op-1",
                             "--generation", "g2", "--host-id", "host-a",
                             "--instance-id", "instance-a", "--credential-kind", "provider_api_key",
                             "--authority-url", "https://issuer.example/current",
                             "--authority-token-file", str(private), "--directory", str(envelope_dir),
                             "--envelope-ref", "env_fixture123",
                             "--host-private-key-file", str(private)])
            self.assertEqual(code, 1)
            self.assertFalse(target.exists())
            self.assertTrue(envelope.exists())
            self.assertNotIn("ciphertext", error.getvalue())


class RotationTests(unittest.TestCase):
    def test_api_probe_binds_candidate_and_keeps_bearer_out_of_arguments(self):
        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "custody"
            parent.mkdir(mode=0o700)
            token = parent / "runner.token"
            token.write_text("fixture-bearer-123\n")
            token.chmod(0o600)
            ca = parent / "private-ca.pem"
            ca.write_text("fixture-ca")
            calls = []

            def run(args, **kwargs):
                calls.append((args, kwargs.get("input")))
                status = b"401" if len(calls) == 1 else b"404"
                return SimpleNamespace(returncode=0, stdout=status, stderr=b"")

            probe = AuthenticatedTaskServerProbe(
                "https://task-server.wg.internal", "runner-a", ca, token,
                os.getuid(), os.getgid(), run=run)
            self.assertTrue(probe("wg-candidate"))
            self.assertTrue(all("--interface" in args and "wg-candidate" in args
                                for args, _ in calls))
            self.assertTrue(all("fixture-bearer-123" not in " ".join(args)
                                for args, _ in calls))
            self.assertIn(b"fixture-bearer-123", calls[1][1])

    def test_wireguard_key_is_generated_and_stored_only_on_peer(self):
        private = base64.b64encode(b"p" * 32)
        public = base64.b64encode(b"q" * 32)
        calls = []

        def run(args, **kwargs):
            calls.append(args)
            return SimpleNamespace(returncode=0,
                                   stdout=(private if args == ["wg", "genkey"] else public) + b"\n",
                                   stderr=b"")

        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "wireguard"
            parent.mkdir(mode=0o700)
            path = parent / "candidate.key"
            public_key, receipt = generate_wireguard_keypair(
                path, os.getuid(), os.getgid(), "operation-1", "generation-2", run=run)
            self.assertEqual(public_key, public.decode())
            self.assertEqual(path.read_bytes(), private + b"\n")
            self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
            self.assertNotIn(private.decode(), repr(receipt))
            self.assertEqual(calls, [["wg", "genkey"], ["wg", "pubkey"]])

    def test_gateway_adapter_persists_peer_changes(self):
        old = base64.b64encode(b"a" * 32).decode()
        candidate = base64.b64encode(b"b" * 32).decode()
        commands = []
        persisted = []

        def run(args, **kwargs):
            commands.append(args)
            dump = "private\tpublic\t51820\toff\n" + old + "\t(none)\t(none)\t10.60.0.2/32\t0\t0\t0\toff\n"
            return SimpleNamespace(returncode=0, stdout=dump, stderr="")

        gateway = LinuxWireGuardGateway(
            "wg0", lambda key, routes: persisted.append((key, routes)), run=run)
        self.assertEqual(gateway.peers(), {old: ["10.60.0.2/32"]})
        with self.assertRaises(SecretPolicyError):
            gateway.add(candidate, ["10.60.0.2/32"])
        gateway.add(candidate, ["10.60.0.3/32"])
        gateway.remove(old)
        self.assertEqual(persisted,
                         [(candidate, ["10.60.0.3/32"]), (old, None)])
        self.assertIn(["wg", "set", "wg0", "peer", candidate,
                       "allowed-ips", "10.60.0.3/32"], commands)

    def test_host_wireguard_adapter_reads_gateway_handshake_and_scopes_probe(self):
        calls = []

        def run(args, **kwargs):
            calls.append(args)
            return SimpleNamespace(returncode=0, stdout="gateway-public\t101\n", stderr="")

        peer = HostWireGuardPeer(
            interface_for=lambda ref: "wg-candidate", gateway_public_key="gateway-public",
            stage_tunnel=lambda ref: calls.append(("stage", ref)),
            discard_tunnel=lambda ref: calls.append(("discard", ref)),
            api_probe=lambda interface: interface == "wg-candidate",
            switch_route=lambda ref: calls.append(("switch", ref)), run=run)
        peer.stage("candidate")
        self.assertTrue(peer.authenticated_api("candidate"))
        self.assertEqual(peer.latest_handshake_at("candidate"), 101)
        peer.activate("candidate")
        self.assertIn(["wg", "show", "wg-candidate", "latest-handshakes"], calls)

    def test_restored_host_cannot_reuse_revoked_or_replaced_authority(self):
        class Source:
            record = ("instance-new", "generation-new", False)

            def current(self, host_id, kind):
                return self.record

        source = Source()
        with self.assertRaises(SecretPolicyError):
            require_current_host_authority(source, "host-a", "wireguard_peer",
                                           "instance-old", "generation-old")
        source.record = ("instance-old", "generation-old", True)
        with self.assertRaises(SecretPolicyError):
            require_current_host_authority(source, "host-a", "administration_ssh_key",
                                           "instance-old", "generation-old")
        source.record = ("instance-new", "generation-new", False)
        require_current_host_authority(source, "host-a", "wireguard_peer",
                                       "instance-new", "generation-new")

    def test_wireguard_candidate_has_separate_address_and_proves_api(self):
        events = []

        class Gateway:
            def peers(self):
                return {"old": ["10.60.0.2/32"]}

            def add(self, peer, routes):
                events.append(("add", peer, routes))

            def remove(self, peer):
                events.append(("remove", peer))

        class Peer:
            def path_identity(self, peer):
                return peer

            def stage(self, peer):
                events.append(("stage", peer))

            def discard(self, peer):
                events.append(("discard", peer))

            def latest_handshake_at(self, peer):
                return 101

            def authenticated_api(self, peer):
                return True

            def activate(self, peer):
                events.append(("activate", peer))

        receipt = WireGuardRotation(Gateway(), Peer(), current_authority("wireguard_peer"), clock=lambda: 100).rotate(
            "op-1", "old", "candidate", ["10.60.0.3/32"])
        self.assertEqual(receipt.outcome, "retired")
        self.assertLess(events.index(("activate", "candidate")), events.index(("remove", "old")))

    def test_wireguard_candidate_cannot_reuse_old_interface(self):
        class Gateway:
            def peers(self):
                return {"old": ["10.60.0.2/32"]}

        class Peer:
            def path_identity(self, peer):
                return "wg0"

            def stage(self, peer):
                raise AssertionError("same interface must fail before staging")

        with self.assertRaisesRegex(SecretPolicyError, "separate interface"):
            WireGuardRotation(Gateway(), Peer(), current_authority("wireguard_peer")).rotate(
                "op-1", "old", "candidate", ["10.60.0.3/32"])

    def test_wireguard_conflict_and_failed_proof_keep_old_peer(self):
        removed = []

        class Gateway:
            def peers(self):
                return {"old": ["10.60.0.2/32"]}

            def add(self, peer, routes):
                pass

            def remove(self, peer):
                removed.append(peer)

        class Peer:
            def path_identity(self, peer):
                return peer

            def stage(self, peer):
                pass

            def discard(self, peer):
                pass

            def latest_handshake_at(self, peer):
                return 101

            def authenticated_api(self, peer):
                return False

            def activate(self, peer):
                raise AssertionError("must not switch")

        rotator = WireGuardRotation(Gateway(), Peer(), current_authority("wireguard_peer"), clock=lambda: 100)
        with self.assertRaises(SecretPolicyError):
            rotator.rotate("op-1", "old", "candidate", ["10.60.0.2/32"])
        receipt = rotator.rotate("op-2", "old", "candidate", ["10.60.0.3/32"])
        self.assertEqual(receipt.outcome, "old-route-retained")
        self.assertNotIn("old", removed)

    def test_wireguard_stale_handshake_cannot_retire_old_peer(self):
        removed = []

        class Gateway:
            def peers(self):
                return {"old": ["10.60.0.2/32"]}

            def add(self, peer, routes):
                pass

            def remove(self, peer):
                removed.append(peer)

        class Peer:
            def path_identity(self, peer):
                return peer

            def stage(self, peer):
                pass

            def discard(self, peer):
                pass

            def latest_handshake_at(self, peer):
                return 99

            def authenticated_api(self, peer):
                return True

            def activate(self, peer):
                raise AssertionError("stale tunnel must not become active")

        receipt = WireGuardRotation(Gateway(), Peer(), current_authority("wireguard_peer"), clock=lambda: 100).rotate(
            "op-1", "old", "candidate", ["10.60.0.3/32"])
        self.assertEqual(receipt.outcome, "old-route-retained")
        self.assertEqual(removed, ["candidate"])

    def test_wireguard_post_cutover_failure_restores_old_route(self):
        routes = {"old": ["10.60.0.2/32"]}
        active = ["old"]
        api_calls = []

        class Gateway:
            def peers(self):
                return dict(routes)

            def add(self, peer, allowed):
                routes[peer] = allowed

            def remove(self, peer):
                routes.pop(peer)

        class Peer:
            def path_identity(self, peer):
                return peer

            def stage(self, peer):
                pass

            def discard(self, peer):
                pass

            def latest_handshake_at(self, peer):
                return 101

            def authenticated_api(self, peer):
                api_calls.append(peer)
                return peer == "old" or api_calls.count("candidate") == 1

            def activate(self, peer):
                active[0] = peer

        receipt = WireGuardRotation(Gateway(), Peer(), current_authority("wireguard_peer"), clock=lambda: 100).rotate(
            "op-1", "old", "candidate", ["10.60.0.3/32"])
        self.assertEqual(receipt.outcome, "old-route-retained")
        self.assertEqual(active[0], "old")
        self.assertEqual(routes, {"old": ["10.60.0.2/32"]})

    def test_ssh_rotation_requires_fresh_pinned_new_key_access(self):
        events = []

        class Ssh:
            def inventory(self):
                return {"old-fingerprint": "owner-a"}

            def add_public_key(self, key):
                events.append("add")

            def prove_pinned_new_key(self, fingerprint):
                events.append("prove")
                return False

            def select_new_key(self, fingerprint):
                events.append("select")

            def remove_public_key(self, fingerprint):
                events.append("remove")

        receipt = SshKeyRotation(Ssh(), current_authority("administration_ssh_key")).rotate(
            "op-1", "old-fingerprint", "new-fingerprint", "ssh-ed25519 fixture", "owner-a")
        self.assertEqual(receipt.outcome, "old-access-retained")
        self.assertEqual(events, ["add", "prove"])

    def test_ssh_old_key_is_retired_after_three_new_key_proofs(self):
        events = []
        authorized = {"old": "owner-a"}

        class Ssh:
            def inventory(self):
                return dict(authorized)

            def add_public_key(self, key):
                events.append("add")

            def prove_pinned_new_key(self, fingerprint):
                events.append("prove")
                return True

            def select_new_key(self, fingerprint):
                events.append("select")

            def remove_public_key(self, fingerprint):
                events.append("remove")
                authorized.pop(fingerprint)

        receipt = SshKeyRotation(Ssh(), current_authority("administration_ssh_key")).rotate(
            "op-1", "old", "new", "ssh-ed25519 fixture", "owner-a")
        self.assertEqual(receipt.outcome, "retired")
        self.assertEqual(events, ["add", "prove", "select", "prove", "remove", "prove"])

    def test_ssh_retirement_is_unverified_if_old_authorization_remains(self):
        class Ssh:
            def inventory(self):
                return {"old": "owner-a"}

            def add_public_key(self, key):
                pass

            def prove_pinned_new_key(self, fingerprint):
                return True

            def select_new_key(self, fingerprint):
                pass

            def remove_public_key(self, fingerprint):
                pass

        receipt = SshKeyRotation(Ssh(), current_authority("administration_ssh_key")).rotate(
            "op-1", "old", "new", "ssh-ed25519 fixture", "owner-a")
        self.assertEqual(receipt.outcome, "retirement-unverified")

    def test_ssh_failed_selected_key_restores_and_proves_old_access(self):
        selected = []
        proofs = []

        class Ssh:
            def inventory(self):
                return {"old": "owner-a"}

            def add_public_key(self, key):
                pass

            def prove_pinned_new_key(self, fingerprint):
                proofs.append(fingerprint)
                return len(proofs) == 1

            def select_new_key(self, fingerprint):
                selected.append("new")

            def restore_old_key(self, fingerprint):
                selected.append("old")
                return True

            def remove_public_key(self, fingerprint):
                raise AssertionError("old access must stay authorized")

        receipt = SshKeyRotation(Ssh(), current_authority("administration_ssh_key")).rotate(
            "op-1", "old", "new", "ssh-ed25519 fixture", "owner-a")
        self.assertEqual(receipt.outcome, "old-access-retained")
        self.assertEqual(selected, ["new", "old"])

    def test_ssh_adapter_pins_host_and_uses_candidate_identity_alone(self):
        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "ssh"
            parent.mkdir(mode=0o700)
            old, new = parent / "old", parent / "new"
            for path in (old, new):
                path.write_text("fixture")
                path.chmod(0o600)
            known = parent / "known_hosts"
            known.write_text("host fixture-key")
            known.chmod(0o600)
            config = parent / "provisioner.conf"
            blob = b"public-fixture"
            public = "ssh-ed25519 " + base64.b64encode(blob).decode() + " fixture"
            fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")
            calls = []

            def run(args, **kwargs):
                calls.append((args, kwargs.get("input", "")))
                return SimpleNamespace(returncode=0, stdout=public + "\n", stderr="")

            adapter = PinnedSshAdministration(
                "host", "admin", known, old, new, config,
                {fingerprint: (public, "owner-a")}, run=run)
            self.assertEqual(adapter.inventory(), {fingerprint: "owner-a"})
            adapter.add_public_key(public)
            self.assertTrue(adapter.prove_pinned_new_key(fingerprint))
            adapter.select_new_key(fingerprint)
            adapter.remove_public_key(fingerprint)
            self.assertEqual(stat.S_IMODE(config.stat().st_mode), 0o600)
            ssh_calls = [(args, stdin) for args, stdin in calls if args[0] == "ssh"]
            self.assertTrue(all("StrictHostKeyChecking=yes" in args for args, _ in ssh_calls))
            self.assertTrue(all("IdentityAgent=none" in args for args, _ in ssh_calls))
            self.assertIn(str(new), ssh_calls[2][0])
            self.assertNotIn(str(old), ssh_calls[2][0])
            self.assertEqual(calls[1][1], public + "\n")

    def test_ssh_remote_script_fails_closed_when_key_input_is_missing(self):
        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "ssh"
            parent.mkdir(mode=0o700)
            old, new, known = (parent / name for name in ("old", "new", "known_hosts"))
            for path in (old, new, known):
                path.write_text("fixture")
                path.chmod(0o600)
            home = parent / "home"
            home.mkdir(mode=0o700)
            authorized = home / ".ssh" / "authorized_keys"
            blob = b"public-fixture"
            public = "ssh-ed25519 " + base64.b64encode(blob).decode() + " fixture"
            fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")

            def remote(args, **kwargs):
                command = " ".join(args[args.index("admin@host") + 1:])
                # SSH sends one command string to the remote login shell. Simulate
                # a dropped stdin stream; read must fail before any key mutation.
                return subprocess.run(["sh", "-c", command], input="", text=True,
                                      capture_output=True, env={**os.environ, "HOME": str(home)})

            adapter = PinnedSshAdministration(
                "host", "admin", known, old, new, parent / "provisioner.conf",
                {fingerprint: (public, "owner-a")}, run=remote)
            with self.assertRaises(SecretPolicyError):
                adapter.add_public_key(public)
            self.assertEqual(authorized.read_text(), "")
            authorized.write_text(public + "\n")
            with self.assertRaises(SecretPolicyError):
                adapter.remove_public_key(fingerprint)
            self.assertEqual(authorized.read_text(), public + "\n")

    def test_ssh_remote_shell_adds_and_retires_exact_public_key(self):
        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "ssh"
            parent.mkdir(mode=0o700)
            old, new, known = (parent / name for name in ("old", "new", "known_hosts"))
            for path in (old, new, known):
                path.write_text("fixture")
                path.chmod(0o600)
            home = parent / "home"
            home.mkdir(mode=0o700)
            blob = b"public-fixture"
            public = "ssh-ed25519 " + base64.b64encode(blob).decode() + " fixture"
            fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")

            def remote(args, **kwargs):
                command = " ".join(args[args.index("admin@host") + 1:])
                return subprocess.run(["sh", "-c", command], input=kwargs["input"],
                                      text=True, capture_output=True,
                                      env={**os.environ, "HOME": str(home)})

            adapter = PinnedSshAdministration(
                "host", "admin", known, old, new, parent / "provisioner.conf",
                {fingerprint: (public, "owner-a")}, run=remote)
            adapter.add_public_key(public)
            authorized = home / ".ssh" / "authorized_keys"
            self.assertEqual(authorized.read_text(), public + "\n")
            adapter.add_public_key(public)
            self.assertEqual(authorized.read_text(), public + "\n")
            adapter.remove_public_key(fingerprint)
            self.assertEqual(authorized.read_text(), "")

    def test_ssh_candidate_identity_must_match_inventoried_fingerprint(self):
        with tempfile.TemporaryDirectory() as root:
            parent = Path(root) / "ssh"
            parent.mkdir(mode=0o700)
            old, new, known = (parent / name for name in ("old", "new", "known_hosts"))
            for path in (old, new, known):
                path.write_text("fixture")
                path.chmod(0o600)
            blob = b"expected-public-fixture"
            public = "ssh-ed25519 " + base64.b64encode(blob).decode()
            fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")
            commands = []

            def run(args, **kwargs):
                commands.append(args)
                return SimpleNamespace(returncode=0, stdout="ssh-ed25519 wrong-fixture\n", stderr="")

            adapter = PinnedSshAdministration(
                "host", "admin", known, old, new, parent / "provisioner.conf",
                {fingerprint: (public, "owner-a")}, run=run)
            self.assertFalse(adapter.prove_pinned_new_key(fingerprint))
            self.assertEqual([args[0] for args in commands], ["ssh-keygen"])


if __name__ == "__main__":
    unittest.main()
