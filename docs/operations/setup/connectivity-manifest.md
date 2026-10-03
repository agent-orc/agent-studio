# Installation connectivity manifest and single link owner

Delivery of Dossier AGT-W63 item I04 (`docs/deployment-story/index.html`, links
table, D4, B2 and C6). Implemented option: **D4 A**, private HTTPS as the target
and supervised reverse SSH as a bounded transition (operator decision of
26 September 2026).

One installation-owned JSON record selects every connectivity value. The server
origin, browser origin, enabled private listeners and any RunnerLinks are all
generated from it by `ConnectivityPolicy`
(`backend/Features/Management/Connectivity/InstallationConnectivity.cs`).
Examples live in [`deploy/connectivity/`](../../../deploy/connectivity/).

## Who owns supervision

Proven from service registration, not assumed:

| Package (`package`) | Process | Registers `LinkSupervisor` |
|---|---|---|
| `legacy-backend` | `backend/Host/Program.cs` | Yes (the only one) |
| `standalone-task-server` | `task-server/Program.cs` | No |
| `control-plane-compose` | standalone Task Server image behind Caddy | No |

`linkOwner: link-supervisor` is therefore rejected (`link-owner-package`) for
both standalone packages. A standalone installation reaches remote runners
only through `private-https`.

## Record

| Field | Meaning |
|---|---|
| `installationId`, `revision` | Identity and monotonic revision. Bump `revision` on every edit; runners advertising an older revision are stale. |
| `mode` | `one-box`, `reverse-ssh-transition` or `wireguard-direct`. |
| `serverOrigin` | Loopback in `one-box`/transition; private HTTPS DNS name in `wireguard-direct`. |
| `browserOrigin` | The one same-origin browser edge. |
| `caBundleFile` | Private CA pinned by every runner (required in `wireguard-direct`). |
| `linkOwner` | `none`, `tunnel-keeper` or `link-supervisor`. Exactly one owner while any runner uses `reverse-ssh`. |
| `enabledListeners` | Subset of the catalogue allowed for the mode. |
| `portOverrides` | Explicit overrides; resolved ports must not collide. |
| `runners[]` | One route per runner: `loopback`, `reverse-ssh` (with `sshTarget`, `knownHostsFile`) or `private-https`. |

No credential is stored in the record or in generated configuration.

## Resolved ports

| Listener | Port | Owner | Bind | `one-box` | transition | `wireguard-direct` |
|---|---|---|---|---|---|---|
| legacy-api | 5031 | backend/Host | 127.0.0.1 | legacy package only | yes | no |
| standalone-task-server | 5071 | task-server | 127.0.0.1 / internal network | standalone | no | yes (internal) |
| studio-bff | 5072 | Studio BFF | 127.0.0.1 | standalone | no | optional |
| browser | 4011 | web edge | 127.0.0.1 | yes | yes | optional |
| runner-tunnel | 15031 | sshd on runner host | runner 127.0.0.1 | no | yes | no |
| local-updater | 5039 | update-service | 127.0.0.1 | optional | optional | no |
| private-edge | 443 | Caddy | WireGuard address | no | no | yes |

Anything outside a mode's column fails validation (`listener-not-allowed`).
TCP 5030 is the development networked backend and is never part of an
installation. `verify-no-public-listener.sh` checks all of the ports above.

## Reverse SSH direction (transition only)

The supervisor host opens SSH to the runner:
`ssh -N ... -o StrictHostKeyChecking=yes -o UserKnownHostsFile=<knownHostsFile>
-R 127.0.0.1:15031:127.0.0.1:5031 <sshTarget>`. The runner calls its own
loopback `http://127.0.0.1:15031`, which forwards to legacy API 5031 on the
supervisor host. The remote bind is explicitly loopback, so `GatewayPorts`
cannot expose it. Reconnect uses the bounded backoff list (default
5, 10, 30, 60, 120 s; each value 1 to 3600 s).

## Enabling the manifest on backend/Host

Set `Connectivity:ManifestPath` to the record. RunnerLinks are then read only
from the record; any enabled inline `RunnerLinks` entry stops the boot with
"the manifest is the single owner". With `linkOwner: tunnel-keeper` no
RunnerLinks are generated, so the Windows TunnelKeeper stays the sole owner.

## Adoption, soak and retirement

These gates belong to AGT-2764 (adoption/soak/retirement) and AGT-2737
(cutover). This document does not waive or duplicate their production
evidence.

1. Before adoption: `linkOwner: tunnel-keeper`. Run
   `probe-runner-route.sh` on the runner host.
2. Adoption: set `linkOwner: link-supervisor`, bump `revision`, restart
   backend/Host. `LinkSupervisor` adopts the healthy existing forward
   (`lastProbe.kind = adoption`). Disable the TunnelKeeper scheduled task in
   the same window. The probe fails with `duplicated-listener` if both run.
3. Soak for the period agreed in AGT-2764. Only after it passes, remove the
   TunnelKeeper registration. The scripts under
   `deploy/windows/agent-runner-tunnel/` remain as the documented emergency
   rollback until then.
4. Direct WireGuard (AGT-2737): switch the record to `wireguard-direct`,
   set each runner's server URL to `serverOrigin`, reconcile active attempts,
   then remove its reverse route. `linkOwner` becomes `none`. No unmonitored
   emergency tunnel is retained as a second production route.

## Host-side proof

On each runner host, as the runner service user:

```sh
deploy/connectivity/probe-runner-route.sh /etc/agent-runner/connectivity.json \
    agent-runner-01 /etc/agent-runner/runner.token
```

It proves health, anonymous `401` plus scoped-credential acceptance, a single
loopback tunnel listener (reverse-ssh), or private DNS, CA-pinned TLS and
certificate lifetime (private-https). It never prints the credential. In
`wireguard-direct` the workstation is not on the path: the runner talks to the
private edge only.

## Network and administration key renewal

AGT-W67 I7 uses D1 option A (host custody with central metadata) and D3
option A (authorized platform rotation). The promoted dependencies are
AGT-2971 (registry) and AGT-2976 (principal delivery). This is a host
adapter contract at the accepted AGT-W63 private-HTTPS placement and AGT-W65
Task Server command boundary. AGT-2945 continues to own the installation
connectivity record and single link owner; this section does not introduce a
second route authority.

For a WireGuard peer (R7), generate its private key on the peer endpoint and
inventory the public key, gateway peer, allowed IPs, owner, and separate
preshared-key reference. Stage a second tunnel with its own peer address and
nonoverlapping gateway allowed IPs and a distinct local interface.
`WireGuardRotation` checks the gateway
inventory before enrolment, then requires a fresh handshake and an
authenticated Task Server API call through the candidate. It switches the
route and repeats the authenticated call before removing the old peer.
`generate_wireguard_keypair` writes the private key locally at `0600` and
returns only the public key and an operation receipt.
`LinuxWireGuardGateway` limits runtime commands to `wg show` and `wg set`; its
required `persist_change` callback must durably update the installation's
gateway configuration so a reboot cannot restore a retired peer. Keep that
configuration under the installation network owner. If candidate proof fails,
the old route stays active and the candidate is removed. If old-route recovery
cannot be proven after a failed cutover, the result is
`maintenance-recovery-required`, never a claimed rollback. When no separately
addressed path exists, schedule maintenance with tested console access before
changing the old peer. WireGuard keys have a rotation due date, not an
inferred OAuth expiry.

For an administration SSH key (R9), inventory each authorized public key and
its owner before rotation. Generate the new private key at its custodian,
retain the old authorized key, and add only the new public key over the
existing pinned connection. `PinnedSshAdministration` disables agent and
password fallback, uses the selected identity alone, requires
`StrictHostKeyChecking=yes` against the existing known-hosts file, and writes
the provisioner's protected SSH config atomically. `SshKeyRotation` proves a
fresh new-key connection before selection, again after selection, and again
after old-key removal, then confirms the old public key is absent. The host
adapter derives the candidate public key from its private identity and checks
it against the inventoried fingerprint before any SSH proof. A failed proof
after selection restores and verifies the old provisioner identity. A changed server host key requires independent identity
verification; never use automatic trust. Keep a tested console recovery path.

On a lost-host restore, read the current host instance, credential generation,
and revocation state from the live issuer, not the backup. Call
`require_current_host_authority` before mounting restored key material or
enabling a route. A replaced instance, revoked generation, or unavailable
issuer refuses authority and requires re-enrolment. Recreate provider OAuth
on the host instead of copying a refresh session. The central command and
installation records retain operation references and public fingerprints,
not private keys or bearer values.

## Failures and exact remediation

| Code | Symptom | Remediation |
|---|---|---|
| `link-owner-package` | Manifest asks a standalone package to supervise a link | Use `private-https`, or run the link from backend/Host. |
| `link-owner-missing` / single-owner boot error | Reverse route without owner, or inline RunnerLinks plus manifest | Set one `linkOwner`; delete inline `RunnerLinks`. |
| `host-key-pin` | No `knownHostsFile` | `ssh-keyscan` the runner, verify the fingerprint out of band, store it, set `knownHostsFile`. |
| SSH `Host key verification failed` | Runner key changed or unpinned | Confirm the rebuild out of band, replace the pinned entry, `POST /api/v1/management/links/{id}/reconnect`. |
| `dropped-tunnel` | No listener on 15031 | Check `GET /api/v1/management/links`; state `reconnecting` retries on backoff; `blockedBy: remote-listener-held` means a stale sshd holds the port: stop the other owner. |
| `duplicated-listener` | Two listeners on the tunnel port | Stop whichever owner is not `linkOwner`. |
| `exposed-tunnel` | Tunnel bound beyond loopback | `GatewayPorts no` in runner `sshd_config`, reconnect. |
| `listener-not-allowed`, `port-duplicated` | Extra or clashing listener | Remove it from `enabledListeners` or fix `portOverrides`. |
| `private-dns` | Origin host does not resolve | Fix WireGuard DNS or the hosts entry; `wg show` for the handshake. |
| `private-trust`, `tls-untrusted` | CA missing or wrong | Distribute the private CA to `caBundleFile`, or reissue the edge certificate. |
| `tls-expired`, `tls-expiring` | Edge certificate expired or close | Renew, restart the edge, rerun the probe. Never use `--insecure`. |
| `auth-open` | Anonymous request not refused | Set `AUTH=bearer` before admitting runners. |
| `auth` | Credential rejected | Re-enrol the runner and reprovision its own scoped credential. |
| `advertisement-stale-revision` | Runner on an older record | Update the runner's server URL, restart its service, wait for a fresh advertisement. |
| `advertisement-route-mismatch` | Runner advertises another URL | Set the URL the record selects; reconcile active attempts before retiring the old route. |
| `advertisement-expired` | No fresh advertisement | Run the probe; check link state (reverse-ssh) or DNS/TLS/WireGuard (private-https). |

## Tests

`backend.Tests/InstallationConnectivityTests.cs` covers the policy matrix
(single owner, host-key pin, duplicated listener and port, WireGuard DNS/TLS/CA,
stale and mismatched advertisement, expired certificate, shipped examples).
`ManagementApiTests` covers dropped tunnel and bounded backoff,
adoption, and restart: two boots from the same manifest produce the same
pinned, loopback-bound link, and inline RunnerLinks next to a manifest fail.
