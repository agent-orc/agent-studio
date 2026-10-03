"""Host-side secret custody and network-key rotation boundaries.

Only operation references and public identifiers belong in durable commands.
Adapters receive secret bytes in process memory at the host. This module does
not mint Task Server principals or change the command bus.
"""

from __future__ import annotations

import base64
import argparse
import binascii
import hashlib
import fcntl
import ipaddress
import json
import os
import re
import secrets
import subprocess
import stat
import sys
import tempfile
import time
from urllib.parse import urlsplit
from dataclasses import dataclass
from dataclasses import asdict
from pathlib import Path
from typing import Callable, Protocol


class SecretPolicyError(RuntimeError):
    """A custody or continuity precondition was not proven."""


@dataclass(frozen=True)
class SecretReceipt:
    operation_ref: str
    generation: str
    adapter: str
    local_ref: str


@dataclass(frozen=True)
class RotationReceipt:
    operation_ref: str
    outcome: str
    candidate_ref: str


class HostAuthoritySource(Protocol):
    """Current issuer state, read outside the restored host's backup set."""

    def current(self, host_id: str, credential_kind: str) -> tuple[str, str, bool] | None: ...


def require_current_host_authority(source: HostAuthoritySource, host_id: str,
                                   credential_kind: str, instance_id: str,
                                   generation: str) -> None:
    """Fence restored host data before any secret is mounted or route enabled.

    The tuple is authoritative (instance id, generation, revoked). Absence,
    revocation, or an older host instance requires re-enrolment through the
    installation owner. Backup metadata cannot grant authority by itself.
    """
    current = source.current(host_id, credential_kind)
    if (current is None or current[2] or current[0] != instance_id
            or current[1] != generation):
        raise SecretPolicyError("Restored host lacks current credential authority")


def _check_file(path: Path, uid: int, gid: int, mode: int) -> None:
    observed = path.lstat()
    if (not stat.S_ISREG(observed.st_mode) or observed.st_uid != uid
            or observed.st_gid != gid or stat.S_IMODE(observed.st_mode) != mode):
        raise SecretPolicyError("Secret file ownership, type or mode is invalid")


def _check_directory(path: Path, uid: int, gid: int, mode: int) -> None:
    observed = path.lstat()
    if (not stat.S_ISDIR(observed.st_mode) or observed.st_uid != uid
            or observed.st_gid != gid or stat.S_IMODE(observed.st_mode) != mode):
        raise SecretPolicyError("Secret directory ownership, type or mode is invalid")


class FileSecretAdapter:
    """Durable local replacement for native 0600 and restricted 0640 stores.

    The caller owns directory creation and service identity selection. Keeping
    that explicit prevents the adapter from silently repairing an unsafe mount.
    """

    def __init__(self, path: Path, uid: int, gid: int, directory_mode: int,
                 file_mode: int, validator: Callable[[bytes], None] | None = None):
        self.path = Path(path)
        self.uid = uid
        self.gid = gid
        self.directory_mode = directory_mode
        self.file_mode = file_mode
        self.validator = validator
        if (directory_mode, file_mode) not in ((0o700, 0o600), (0o750, 0o640)):
            raise SecretPolicyError("Unsupported host secret permission profile")

    def install(self, value: bytes, operation_ref: str, generation: str) -> SecretReceipt:
        if not value or not operation_ref or not generation:
            raise SecretPolicyError("A nonempty value, operation and generation are required")
        if self.validator is not None:
            self.validator(value)
        parent = self.path.parent
        _check_directory(parent, self.uid, self.gid, self.directory_mode)
        lock_path = parent / ("." + self.path.name + ".lock")
        lock_fd = os.open(lock_path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
        try:
            fcntl.flock(lock_fd, fcntl.LOCK_EX)
            os.fchmod(lock_fd, 0o600)
            if os.geteuid() == 0:
                os.fchown(lock_fd, self.uid, self.gid)
            _check_file(lock_path, self.uid, self.gid, 0o600)
            if self.path.is_symlink() or self.path.exists():
                _check_file(self.path, self.uid, self.gid, self.file_mode)
            fd, temporary = tempfile.mkstemp(prefix="." + self.path.name + ".", dir=parent)
            try:
                os.fchmod(fd, self.file_mode)
                if os.geteuid() == 0:
                    os.fchown(fd, self.uid, self.gid)
                with os.fdopen(fd, "wb") as output:
                    output.write(value)
                    output.flush()
                    os.fsync(output.fileno())
                _check_file(Path(temporary), self.uid, self.gid, self.file_mode)
                os.replace(temporary, self.path)
                dir_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(dir_fd)
                finally:
                    os.close(dir_fd)
                _check_file(self.path, self.uid, self.gid, self.file_mode)
            finally:
                if os.path.exists(temporary):
                    os.unlink(temporary)
            return SecretReceipt(operation_ref, generation, "restricted-file", str(self.path))
        finally:
            fcntl.flock(lock_fd, fcntl.LOCK_UN)
            os.close(lock_fd)


class NativeCliStoreAdapter(FileSecretAdapter):
    def __init__(self, path: Path, service_uid: int, service_gid: int):
        def validate_json(value: bytes) -> None:
            try:
                if not isinstance(json.loads(value), dict):
                    raise ValueError("Native store must be an object")
            except (UnicodeDecodeError, ValueError) as exc:
                raise SecretPolicyError("Native store format is invalid") from exc
        super().__init__(path, service_uid, service_gid, 0o700, 0o600, validate_json)


class EnvironmentFileAdapter(FileSecretAdapter):
    def __init__(self, path: Path, root_uid: int, agent_gid: int):
        def validate_env(value: bytes) -> None:
            try:
                lines = value.decode("utf-8").splitlines()
            except UnicodeDecodeError as exc:
                raise SecretPolicyError("Environment file encoding is invalid") from exc
            keys = set()
            for line in lines:
                if not line or line.startswith("#"):
                    continue
                key, separator, _ = line.partition("=")
                if (not separator or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key)
                        or key in keys or "\x00" in line):
                    raise SecretPolicyError("Environment file format is invalid")
                keys.add(key)
        super().__init__(path, root_uid, agent_gid, 0o750, 0o640, validate_env)


class DockerSecretMountAdapter:
    """Read-only file view; replacement of a bind-mounted inode needs a roll."""

    def __init__(self, path: Path, uid: int, gid: int):
        self.path = Path(path)
        self.uid = uid
        self.gid = gid

    def inode(self) -> int:
        observed = self.path.lstat()
        if (not stat.S_ISREG(observed.st_mode) or observed.st_uid != self.uid
                or observed.st_gid != self.gid
                or not os.statvfs(self.path).f_flag & os.ST_RDONLY):
            raise SecretPolicyError("Service secret mount is not a read-only regular file")
        return observed.st_ino

    def read(self) -> bytes:
        self.inode()
        return self.path.read_bytes()


class ExternalSecretStore(Protocol):
    """Optional host-local external store. Implementations enforce own ACLs."""

    def install(self, host_id: str, local_ref: str, value: bytes,
                operation_ref: str, generation: str) -> SecretReceipt: ...


_REFERENCE = re.compile(r"^[A-Za-z0-9_-]{8,128}$")


class DeliveryEnvelopeStore:
    """Single-use encrypted host delivery, separate from durable commands.

    Requires cryptography on the issuing and receiving hosts. X25519 binds the
    payload to the enrolled host key; AES-GCM binds host, operation, generation
    and expiry. The store holds ciphertext only. A failed consume after claim
    needs a fresh envelope for the same operation, never a second issuance.
    """

    def __init__(self, directory: Path, clock: Callable[[], float] = time.time):
        self.directory = Path(directory)
        self.clock = clock
        if not self.directory.is_dir() or self.directory.is_symlink():
            raise SecretPolicyError("Envelope directory is unavailable")
        if stat.S_IMODE(self.directory.stat().st_mode) != 0o700:
            raise SecretPolicyError("Envelope directory must be mode 0700")

    def _path(self, ref: str) -> Path:
        if not _REFERENCE.fullmatch(ref):
            raise SecretPolicyError("Invalid envelope reference")
        return self.directory / (ref + ".json")

    def issue(self, host_id: str, operation_ref: str, generation: str,
              ttl_seconds: int, value: bytes, host_public_key: bytes) -> str:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        from cryptography.hazmat.primitives.kdf.hkdf import HKDF
        from cryptography.hazmat.primitives import hashes

        if not all((host_id, operation_ref, generation, value)) or not 1 <= ttl_seconds <= 3600:
            raise SecretPolicyError("Invalid envelope binding or lifetime")
        ref = "env_" + secrets.token_urlsafe(24)
        expires_at = int(self.clock()) + ttl_seconds
        binding = {"hostId": host_id, "operationRef": operation_ref,
                   "generation": generation, "expiresAt": expires_at}
        aad = json.dumps(binding, sort_keys=True, separators=(",", ":")).encode()
        ephemeral = X25519PrivateKey.generate()
        shared = ephemeral.exchange(X25519PublicKey.from_public_bytes(host_public_key))
        key = HKDF(algorithm=hashes.SHA256(), length=32, salt=None,
                   info=b"agent-studio-host-delivery-v1" + aad).derive(shared)
        nonce = os.urandom(12)
        payload = {**binding,
                   "ephemeralPublicKey": base64.b64encode(ephemeral.public_key().public_bytes(
                       serialization.Encoding.Raw, serialization.PublicFormat.Raw)).decode(),
                   "nonce": base64.b64encode(nonce).decode(),
                   "ciphertext": base64.b64encode(AESGCM(key).encrypt(nonce, value, aad)).decode()}
        path = self._path(ref)
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "w") as output:
            json.dump(payload, output, sort_keys=True, separators=(",", ":"))
            output.flush()
            os.fsync(output.fileno())
        dir_fd = os.open(self.directory, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(dir_fd)
        finally:
            os.close(dir_fd)
        return ref

    def consume(self, ref: str, host_id: str, operation_ref: str,
                generation: str, host_private_key) -> bytes:
        from cryptography.exceptions import InvalidTag
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PublicKey
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        from cryptography.hazmat.primitives.kdf.hkdf import HKDF
        from cryptography.hazmat.primitives import hashes

        path = self._path(ref)
        try:
            with path.open("rb") as source:
                payload = json.load(source)
        except (FileNotFoundError, ValueError) as exc:
            raise SecretPolicyError("Envelope is unavailable") from exc
        try:
            binding = {key: payload[key] for key in
                       ("hostId", "operationRef", "generation", "expiresAt")}
        except (KeyError, TypeError) as exc:
            raise SecretPolicyError("Envelope binding is malformed") from exc
        if (binding["hostId"] != host_id or binding["operationRef"] != operation_ref
                or binding["generation"] != generation or self.clock() >= binding["expiresAt"]):
            raise SecretPolicyError("Envelope binding or expiry check failed")
        claimed = self.directory / (ref + ".claimed")
        try:
            os.link(path, claimed, follow_symlinks=False)
            os.unlink(path)
        except FileExistsError as exc:
            raise SecretPolicyError("Envelope already claimed") from exc
        except FileNotFoundError as exc:
            raise SecretPolicyError("Envelope already consumed") from exc
        try:
            aad = json.dumps(binding, sort_keys=True, separators=(",", ":")).encode()
            shared = host_private_key.exchange(X25519PublicKey.from_public_bytes(
                base64.b64decode(payload["ephemeralPublicKey"])))
            key = HKDF(algorithm=hashes.SHA256(), length=32, salt=None,
                       info=b"agent-studio-host-delivery-v1" + aad).derive(shared)
            try:
                return AESGCM(key).decrypt(base64.b64decode(payload["nonce"]),
                                           base64.b64decode(payload["ciphertext"]), aad)
            except (InvalidTag, ValueError, KeyError) as exc:
                raise SecretPolicyError("Envelope authentication failed") from exc
        finally:
            claimed.unlink(missing_ok=True)

    def prune_expired(self) -> int:
        """Remove expired ciphertext and interrupted claims after their TTL."""
        removed = 0
        for path in self.directory.iterdir():
            if path.suffix not in (".json", ".claimed") or path.is_symlink():
                continue
            try:
                payload = json.loads(path.read_text())
                if self.clock() >= payload["expiresAt"]:
                    path.unlink()
                    removed += 1
            except (OSError, ValueError, KeyError):
                continue
        return removed


class WireGuardGateway(Protocol):
    def peers(self) -> dict[str, list[str]]: ...
    def add(self, peer: str, routes: list[str]) -> None: ...
    def remove(self, peer: str) -> None: ...


class WireGuardPeer(Protocol):
    def path_identity(self, peer: str) -> str: ...
    def stage(self, peer: str) -> None: ...
    def discard(self, peer: str) -> None: ...
    def latest_handshake_at(self, peer: str) -> int | None: ...
    def authenticated_api(self, peer: str) -> bool: ...
    def activate(self, peer: str) -> None: ...


def generate_wireguard_keypair(private_path: Path, uid: int, gid: int,
                               operation_ref: str, generation: str,
                               run=subprocess.run) -> tuple[str, SecretReceipt]:
    """Generate on the peer; return only its public key and local receipt."""
    generated = run(["wg", "genkey"], capture_output=True, timeout=10, check=False)
    if generated.returncode != 0 or not generated.stdout:
        raise SecretPolicyError("WireGuard private-key generation failed")
    private_value = generated.stdout.strip()
    if not re.fullmatch(rb"[A-Za-z0-9+/]{43}=", private_value):
        raise SecretPolicyError("WireGuard generated key format is invalid")
    public = run(["wg", "pubkey"], input=private_value + b"\n",
                 capture_output=True, timeout=10, check=False)
    if public.returncode != 0 or not re.fullmatch(rb"[A-Za-z0-9+/]{43}=", public.stdout.strip()):
        raise SecretPolicyError("WireGuard public-key derivation failed")
    receipt = FileSecretAdapter(private_path, uid, gid, 0o700, 0o600).install(
        private_value + b"\n", operation_ref, generation)
    return public.stdout.strip().decode("ascii"), receipt


class WireGuardRotation:
    def __init__(self, gateway: WireGuardGateway, peer: WireGuardPeer,
                 clock: Callable[[], float] = time.time):
        self.gateway = gateway
        self.peer = peer
        self.clock = clock

    def rotate(self, operation_ref: str, old: str, candidate: str,
               routes: list[str]) -> RotationReceipt:
        if not operation_ref or not old or not candidate or old == candidate or not routes:
            raise SecretPolicyError("A distinct candidate peer is required")
        existing = self.gateway.peers()
        if old not in existing or candidate in existing:
            raise SecretPolicyError("Old peer authority or candidate state is uncertain")
        if self.peer.path_identity(old) == self.peer.path_identity(candidate):
            raise SecretPolicyError("Candidate tunnel must use a separate interface")
        try:
            proposed = [ipaddress.ip_network(route, strict=False) for route in routes]
            current_routes = [ipaddress.ip_network(route, strict=False)
                              for peer_routes in existing.values() for route in peer_routes]
        except ValueError as exc:
            raise SecretPolicyError("WireGuard allowed IP inventory is invalid") from exc
        if any(new.overlaps(current) for new in proposed for current in current_routes):
            raise SecretPolicyError("Second path unavailable; require maintenance and tested console recovery")

        def discard_candidate() -> bool:
            try:
                self.gateway.remove(candidate)
                self.peer.discard(candidate)
                return True
            except Exception:
                return False

        staged_at = int(self.clock())
        self.peer.stage(candidate)
        try:
            self.gateway.add(candidate, routes)
        except Exception:
            return RotationReceipt(operation_ref,
                                   "old-route-retained" if discard_candidate()
                                   else "maintenance-recovery-required", candidate)
        switched = False
        try:
            api_proven = self.peer.authenticated_api(candidate)
            handshake_at = self.peer.latest_handshake_at(candidate)
            if (not api_proven or handshake_at is None or handshake_at < staged_at):
                return RotationReceipt(operation_ref,
                                       "old-route-retained" if discard_candidate()
                                       else "maintenance-recovery-required", candidate)
            switched = True
            self.peer.activate(candidate)
            if not self.peer.authenticated_api(candidate):
                raise SecretPolicyError("Candidate API proof failed after cutover")
            self.gateway.remove(old)
            self.peer.discard(old)
            return RotationReceipt(operation_ref, "retired", candidate)
        except Exception:
            if switched:
                try:
                    if old not in self.gateway.peers():
                        self.gateway.add(old, existing[old])
                    self.peer.activate(old)
                    old_proven = self.peer.authenticated_api(old)
                except Exception:
                    old_proven = False
                if not old_proven:
                    return RotationReceipt(operation_ref, "maintenance-recovery-required", candidate)
            return RotationReceipt(operation_ref,
                                   "old-route-retained" if discard_candidate()
                                   else "maintenance-recovery-required", candidate)


class LinuxWireGuardGateway:
    """Allowlisted ``wg`` operations for an already authorized gateway host."""

    def __init__(self, interface: str, persist_change: Callable[[str, list[str] | None], None],
                 preshared_key_file: Path | None = None, run=subprocess.run):
        if not re.fullmatch(r"[A-Za-z0-9_.-]{1,15}", interface):
            raise SecretPolicyError("Invalid WireGuard interface")
        self.interface = interface
        self.preshared_key_file = Path(preshared_key_file) if preshared_key_file else None
        self.persist_change = persist_change
        self.run = run

    def peers(self) -> dict[str, list[str]]:
        result = self.run(["wg", "show", self.interface, "dump"], text=True,
                          capture_output=True, timeout=10, check=False)
        if result.returncode != 0:
            raise SecretPolicyError("Could not read gateway peer inventory")
        peers = {}
        for line in result.stdout.splitlines()[1:]:
            fields = line.split("\t")
            if len(fields) < 4:
                raise SecretPolicyError("Gateway peer inventory is malformed")
            peers[fields[0]] = [] if fields[3] == "(none)" else fields[3].split(",")
        return peers

    def add(self, peer: str, routes: list[str]) -> None:
        if not re.fullmatch(r"[A-Za-z0-9+/]{43}=", peer):
            raise SecretPolicyError("Candidate WireGuard public key is invalid")
        try:
            proposed = [ipaddress.ip_network(route, strict=False) for route in routes]
            current = [ipaddress.ip_network(route, strict=False)
                       for key, assigned in self.peers().items() if key != peer
                       for route in assigned]
        except ValueError as exc:
            raise SecretPolicyError("WireGuard allowed IP inventory is invalid") from exc
        if any(new.overlaps(old) for new in proposed for old in current):
            raise SecretPolicyError("WireGuard allowed IPs conflict with an active peer")
        command = ["wg", "set", self.interface, "peer", peer,
                   "allowed-ips", ",".join(routes)]
        if self.preshared_key_file:
            _check_file(self.preshared_key_file, os.getuid(), os.getgid(), 0o600)
            command += ["preshared-key", str(self.preshared_key_file)]
        if self.run(command, text=True, capture_output=True, timeout=10,
                    check=False).returncode != 0:
            raise SecretPolicyError("Could not enroll candidate gateway peer")
        try:
            self.persist_change(peer, routes)
        except Exception:
            self.run(["wg", "set", self.interface, "peer", peer, "remove"],
                     text=True, capture_output=True, timeout=10, check=False)
            raise

    def remove(self, peer: str) -> None:
        if not re.fullmatch(r"[A-Za-z0-9+/]{43}=", peer):
            raise SecretPolicyError("WireGuard public key is invalid")
        if self.run(["wg", "set", self.interface, "peer", peer, "remove"],
                    text=True, capture_output=True, timeout=10,
                    check=False).returncode != 0:
            raise SecretPolicyError("Could not remove gateway peer")
        self.persist_change(peer, None)


class HostWireGuardPeer:
    """Endpoint proof adapter for an installation-owned tunnel manager.

    ``stage_tunnel`` creates the separately addressed candidate, ``api_probe``
    must bind its authenticated request to that candidate interface, and
    ``switch_route`` changes only the installation's selected route. None may
    fall back silently to the old tunnel. The gateway public key and interface
    mapping come from the installation connectivity record.
    """

    def __init__(self, interface_for: Callable[[str], str], gateway_public_key: str,
                 stage_tunnel: Callable[[str], None],
                 discard_tunnel: Callable[[str], None],
                 api_probe: Callable[[str], bool],
                 switch_route: Callable[[str], None], run=subprocess.run):
        self.interface_for = interface_for
        self.gateway_public_key = gateway_public_key
        self.stage_tunnel = stage_tunnel
        self.discard_tunnel = discard_tunnel
        self.api_probe = api_probe
        self.switch_route = switch_route
        self.run = run

    def path_identity(self, peer: str) -> str:
        interface = self.interface_for(peer)
        if not re.fullmatch(r"[A-Za-z0-9_.-]{1,15}", interface):
            raise SecretPolicyError("Invalid WireGuard peer interface")
        return interface

    def stage(self, peer: str) -> None:
        self.stage_tunnel(peer)

    def discard(self, peer: str) -> None:
        self.discard_tunnel(peer)

    def latest_handshake_at(self, peer: str) -> int | None:
        interface = self.path_identity(peer)
        result = self.run(["wg", "show", interface, "latest-handshakes"],
                          text=True, capture_output=True, timeout=10,
                          check=False)
        if result.returncode != 0:
            return None
        for line in result.stdout.splitlines():
            fields = line.split("\t")
            if len(fields) == 2 and fields[0] == self.gateway_public_key:
                try:
                    value = int(fields[1])
                    return value if value > 0 else None
                except ValueError:
                    return None
        return None

    def authenticated_api(self, peer: str) -> bool:
        return self.api_probe(self.path_identity(peer))

    def activate(self, peer: str) -> None:
        self.switch_route(peer)


class AuthenticatedTaskServerProbe:
    """Candidate-interface API proof with CA pinning and stdin-only bearer."""

    def __init__(self, server_origin: str, runner_id: str, ca_bundle: Path,
                 token_file: Path, uid: int, gid: int, run=subprocess.run):
        parsed = urlsplit(server_origin)
        if (parsed.scheme != "https" or not parsed.netloc or parsed.username
                or parsed.password or parsed.path not in ("", "/")
                or not re.fullmatch(r"[A-Za-z0-9_.-]+", runner_id)):
            raise SecretPolicyError("Invalid private Task Server probe target")
        self.url = server_origin.rstrip("/") + "/api/v1/runners/" + runner_id
        self.ca_bundle = Path(ca_bundle)
        self.token_file = Path(token_file)
        self.uid, self.gid = uid, gid
        self.run = run

    def __call__(self, interface: str) -> bool:
        if not re.fullmatch(r"[A-Za-z0-9_.-]{1,15}", interface):
            raise SecretPolicyError("Invalid candidate probe interface")
        _check_file(self.token_file, self.uid, self.gid, 0o600)
        if not self.ca_bundle.is_file():
            raise SecretPolicyError("Pinned private CA bundle is unavailable")
        token = self.token_file.read_bytes().strip()
        if not re.fullmatch(rb"[A-Za-z0-9._~+/=-]+", token):
            raise SecretPolicyError("Scoped API credential format is invalid")
        common = ["curl", "--silent", "--output", "/dev/null",
                  "--write-out", "%{http_code}", "--max-time", "10",
                  "--cacert", str(self.ca_bundle), "--interface", interface]
        anonymous = self.run([*common, self.url], capture_output=True,
                             timeout=12, check=False)
        if anonymous.returncode != 0 or anonymous.stdout.strip() != b"401":
            return False
        config = b'header = "Authorization: Bearer ' + token + b'"\n'
        authenticated = self.run([*common, "--config", "-", self.url],
                                 input=config, capture_output=True,
                                 timeout=12, check=False)
        return (authenticated.returncode == 0 and
                authenticated.stdout.strip() in (b"200", b"204", b"404", b"405"))


class SshAdministration(Protocol):
    def inventory(self) -> dict[str, str]: ...
    def add_public_key(self, key: str) -> None: ...
    def prove_pinned_new_key(self, fingerprint: str) -> bool: ...
    def select_new_key(self, fingerprint: str) -> None: ...
    def restore_old_key(self, fingerprint: str) -> bool: ...
    def remove_public_key(self, fingerprint: str) -> None: ...


class SshKeyRotation:
    def __init__(self, administration: SshAdministration):
        self.administration = administration

    def rotate(self, operation_ref: str, old_fingerprint: str,
               new_fingerprint: str, new_public_key: str, owner: str) -> RotationReceipt:
        inventory = self.administration.inventory()
        if (not operation_ref or old_fingerprint == new_fingerprint or
                inventory.get(old_fingerprint) != owner or
                new_fingerprint in inventory or not new_public_key.startswith("ssh-ed25519 ")):
            raise SecretPolicyError("Administration key inventory or ownership is invalid")
        self.administration.add_public_key(new_public_key)
        if not self.administration.prove_pinned_new_key(new_fingerprint):
            return RotationReceipt(operation_ref, "old-access-retained", new_fingerprint)
        self.administration.select_new_key(new_fingerprint)
        if not self.administration.prove_pinned_new_key(new_fingerprint):
            restored = self.administration.restore_old_key(old_fingerprint)
            return RotationReceipt(operation_ref,
                                   "old-access-retained" if restored
                                   else "maintenance-recovery-required", new_fingerprint)
        self.administration.remove_public_key(old_fingerprint)
        if not self.administration.prove_pinned_new_key(new_fingerprint):
            return RotationReceipt(operation_ref, "maintenance-recovery-required", new_fingerprint)
        if old_fingerprint in self.administration.inventory():
            return RotationReceipt(operation_ref, "retirement-unverified", new_fingerprint)
        return RotationReceipt(operation_ref, "retired", new_fingerprint)


class PinnedSshAdministration:
    """Linux administration adapter using fresh, pinned, single-identity SSH.

    ``keys`` maps public fingerprints to (authorized key line, owner). The
    protected SSH config file is the provisioner's selection point. Private
    key bytes never enter subprocess arguments or the operation receipt.
    """

    def __init__(self, host: str, user: str, known_hosts: Path,
                 old_identity: Path, new_identity: Path, provisioner_config: Path,
                 keys: dict[str, tuple[str, str]], run=subprocess.run):
        if not re.fullmatch(r"[A-Za-z0-9.-]+", host) or not re.fullmatch(r"[A-Za-z0-9_-]+", user):
            raise SecretPolicyError("Invalid SSH administration endpoint")
        self.host, self.user = host, user
        self.known_hosts = Path(known_hosts)
        self.old_identity, self.new_identity = Path(old_identity), Path(new_identity)
        self.provisioner_config = Path(provisioner_config)
        self.keys = keys
        self.run = run
        self.active_identity = self.old_identity
        for fingerprint, (public_line, _) in keys.items():
            fields = public_line.split()
            try:
                blob = base64.b64decode(fields[1], validate=True)
            except (IndexError, ValueError, binascii.Error) as exc:
                raise SecretPolicyError("Invalid administration public key inventory") from exc
            actual = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")
            if fields[0] != "ssh-ed25519" or actual != fingerprint:
                raise SecretPolicyError("Administration key fingerprint does not match public key")
        for key_path in (self.old_identity, self.new_identity):
            _check_directory(key_path.parent, os.getuid(), os.getgid(), 0o700)
            _check_file(key_path, os.getuid(), os.getgid(), 0o600)
        _check_directory(self.known_hosts.parent, os.getuid(), os.getgid(), 0o700)
        _check_file(self.known_hosts, os.getuid(), os.getgid(), 0o600)
        for path in (self.old_identity, self.new_identity, self.known_hosts):
            if any(char.isspace() for char in str(path)):
                raise SecretPolicyError("SSH custody paths cannot contain whitespace")

    def _ssh(self, identity: Path, script: str, input_text: str = ""):
        command = ["ssh", "-F", "/dev/null", "-o", "BatchMode=yes",
                   "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none",
                   "-o", "PreferredAuthentications=publickey",
                   "-o", "PasswordAuthentication=no",
                   "-o", "StrictHostKeyChecking=yes",
                   "-o", "UpdateHostKeys=no",
                   "-o", f"UserKnownHostsFile={self.known_hosts}",
                   "-i", str(identity), f"{self.user}@{self.host}",
                   "sh", "-c", script]
        return self.run(command, input=input_text, text=True, capture_output=True,
                        timeout=20, check=False)

    def inventory(self) -> dict[str, str]:
        result = self._ssh(self.active_identity, "cat ~/.ssh/authorized_keys")
        if result.returncode != 0:
            raise SecretPolicyError("Could not inventory administration keys through pinned SSH")
        actual = {" ".join(line.split()[:2]) for line in result.stdout.splitlines()
                  if line.startswith("ssh-")}
        return {fingerprint: owner for fingerprint, (line, owner) in self.keys.items()
                if " ".join(line.split()[:2]) in actual}

    def add_public_key(self, key: str) -> None:
        if not key.startswith("ssh-ed25519 ") or "\n" in key:
            raise SecretPolicyError("Invalid administration public key")
        if not any(" ".join(key.split()[:2]) == " ".join(item[0].split()[:2])
                   for item in self.keys.values()):
            raise SecretPolicyError("Administration public key is absent from inventory")
        script = ("set -eu; umask 077; mkdir -p ~/.ssh; chmod 700 ~/.ssh; "
                  "touch ~/.ssh/authorized_keys; chmod 600 ~/.ssh/authorized_keys; "
                  "IFS= read -r key; grep -Fqx -- \"$key\" ~/.ssh/authorized_keys "
                  "|| printf '%s\\n' \"$key\" >> ~/.ssh/authorized_keys")
        if self._ssh(self.active_identity, script, key + "\n").returncode != 0:
            raise SecretPolicyError("Could not stage administration public key")

    def prove_pinned_new_key(self, fingerprint: str) -> bool:
        if fingerprint not in self.keys:
            return False
        derived = self.run(["ssh-keygen", "-y", "-f", str(self.new_identity)],
                           text=True, capture_output=True, timeout=10, check=False)
        if (derived.returncode != 0 or
                " ".join(derived.stdout.split()[:2]) !=
                " ".join(self.keys[fingerprint][0].split()[:2])):
            return False
        return self._ssh(self.new_identity, "true").returncode == 0

    def select_new_key(self, fingerprint: str) -> None:
        if fingerprint not in self.keys:
            raise SecretPolicyError("Unknown candidate administration key")
        config = (f"Host {self.host}\n  HostName {self.host}\n  User {self.user}\n"
                  f"  IdentityFile {self.new_identity}\n  IdentitiesOnly yes\n"
                  f"  IdentityAgent none\n  StrictHostKeyChecking yes\n"
                  f"  UserKnownHostsFile {self.known_hosts}\n  UpdateHostKeys no\n")
        FileSecretAdapter(self.provisioner_config, os.getuid(), os.getgid(),
                          0o700, 0o600).install(config.encode(),
                                                "ssh-provisioner-selection", fingerprint)
        self.active_identity = self.new_identity

    def restore_old_key(self, fingerprint: str) -> bool:
        if fingerprint not in self.keys:
            return False
        config = (f"Host {self.host}\n  HostName {self.host}\n  User {self.user}\n"
                  f"  IdentityFile {self.old_identity}\n  IdentitiesOnly yes\n"
                  f"  IdentityAgent none\n  StrictHostKeyChecking yes\n"
                  f"  UserKnownHostsFile {self.known_hosts}\n  UpdateHostKeys no\n")
        FileSecretAdapter(self.provisioner_config, os.getuid(), os.getgid(),
                          0o700, 0o600).install(config.encode(),
                                                "ssh-provisioner-rollback", fingerprint)
        self.active_identity = self.old_identity
        return self._ssh(self.old_identity, "true").returncode == 0

    def remove_public_key(self, fingerprint: str) -> None:
        line = self.keys[fingerprint][0]
        script = ("set -eu; IFS= read -r key; "
                  "tmp=$(mktemp ~/.ssh/.authorized_keys.XXXXXXXX); "
                  "grep -Fvx -- \"$key\" ~/.ssh/authorized_keys > \"$tmp\" || test $? -eq 1; "
                  "chmod 600 \"$tmp\"; mv \"$tmp\" ~/.ssh/authorized_keys")
        if self._ssh(self.active_identity, script, line + "\n").returncode != 0:
            raise SecretPolicyError("Could not retire old administration public key")


def _adapter_for(profile: str, target: Path, uid: int, gid: int) -> FileSecretAdapter:
    if profile == "native":
        return NativeCliStoreAdapter(target, uid, gid)
    if profile == "environment":
        return EnvironmentFileAdapter(target, uid, gid)
    raise SecretPolicyError("Unknown host secret profile")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Host-owned secret transport")
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("install", "consume-envelope"):
        command = commands.add_parser(name)
        command.add_argument("--profile", choices=("native", "environment"), required=True)
        command.add_argument("--target", type=Path, required=True)
        command.add_argument("--uid", type=int, required=True)
        command.add_argument("--gid", type=int, required=True)
        command.add_argument("--operation-ref", required=True)
        command.add_argument("--generation", required=True)
        if name == "consume-envelope":
            command.add_argument("--directory", type=Path, required=True)
            command.add_argument("--envelope-ref", required=True)
            command.add_argument("--host-id", required=True)
            command.add_argument("--host-private-key-file", type=Path, required=True)
    issue = commands.add_parser("issue-envelope")
    issue.add_argument("--directory", type=Path, required=True)
    issue.add_argument("--host-id", required=True)
    issue.add_argument("--operation-ref", required=True)
    issue.add_argument("--generation", required=True)
    issue.add_argument("--ttl-seconds", type=int, required=True)
    issue.add_argument("--host-public-key-file", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "issue-envelope":
            reference = DeliveryEnvelopeStore(args.directory).issue(
                args.host_id, args.operation_ref, args.generation,
                args.ttl_seconds, sys.stdin.buffer.read(),
                args.host_public_key_file.read_bytes())
            print(json.dumps({"operationRef": args.operation_ref,
                              "envelopeRef": reference}, sort_keys=True))
            return 0
        if args.command == "install":
            value = sys.stdin.buffer.read()
        else:
            from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
            private_file = args.host_private_key_file
            _check_directory(private_file.parent, os.getuid(), os.getgid(), 0o700)
            _check_file(private_file, os.getuid(), os.getgid(), 0o600)
            private_key = X25519PrivateKey.from_private_bytes(private_file.read_bytes())
            value = DeliveryEnvelopeStore(args.directory).consume(
                args.envelope_ref, args.host_id, args.operation_ref,
                args.generation, private_key)
        receipt = _adapter_for(args.profile, args.target, args.uid, args.gid).install(
            value, args.operation_ref, args.generation)
        print(json.dumps(asdict(receipt), sort_keys=True))
        return 0
    except (SecretPolicyError, OSError, ValueError) as exc:
        print(f"Host secret operation refused: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
