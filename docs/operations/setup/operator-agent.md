# Operator agent

<!-- append-only:operator-agent:start -->

The operator agent is the interactive coding agent session you run on your own
machine to operate Studio. It gets its own identity in Studio and its own OS
user on your machine, so its actions are never recorded as yours and it cannot
reach your SSH agent, keys, or credential files (security model SM-1,
decision D11 option A; see the
[security model Dossier](../../security-model/index.html)).

## What you get

| Who | What it holds | Lifetime |
|---|---|---|
| Operator agent principal (`opa_*`) | Role bindings you choose, no token of its own | Until you disable it |
| Operator session | One `oas.*` bearer in the agent user's protected file, bound to its resident key | Chosen at confirmation, limited by role, agent and binding; hourly key renewal by the device. Unbound Settings sessions remain 1 to 8 hours |
| Known device | The key thumbprint of a terminal whose sign-in you confirmed | Until you forget the device |
| Standing grant | The routine (T1) operations the agent may run, by scope, rate, and window | 7 days by default, at most 30, renewed only by you |
| Approval | One exact high-risk (T2) request you approved after a second factor | Used once, within 15 minutes of your decision |
| You, the Owner | Creating the agent, confirming terminal sign-ins with your second factor, starting and ending sessions, managing grants and session limits; the kill switch | |

Every request the session makes, every audit entry, and every card timeline
event it causes names `operator-agent:<id>/session:<sid>` as actor and
`human:<your user id>` as `delegatedBy`. A session token cannot call access
administration, read or verify the audit trail, create role bindings, resolve
secret values, or use the Task Server plane; Studio answers 403.

Every request also passes the policy step (security model SM-4). Reads (T0)
are always allowed to an active session. Routine operations (T1), such as
requeueing or steering a card, draining or resuming a host, re-running a gate,
or deploying a gate-verified commit, run only under a standing grant you
issued. High-risk operations (T2), such as changing secrets, deleting data, or
bypassing a gate, wait for your approval of that exact request in Studio
(security model SM-5). Anything outside the operation catalogue is refused
with `operation-not-catalogued`. A chat message or the agent's memory never
grants anything; only grant and approval records do.

## 1. Create the operator agent in Studio

1. Sign in as the Owner and open **Settings > Access > Operator agents**.
2. Choose **Create operator agent**, give it a name such as `ops console`.
3. Bind the narrowest role that covers the routine work, in **Role bindings**
   with the principal kind service account and the agent's id. Developer on the
   projects it operates is a typical start. Owner cannot be bound. The
   Operator agent role works on the workspace as well as per project: its card
   writes resolve the card's project, so a workspace binding no longer refuses
   them as installation-wide changes (ES-2). Do not bind Admin to accept or
   archive cards with an override; the agent asks you for each one instead
   (step 5).

## 2. Create its own OS user

The agent runs under a dedicated, unprivileged OS user. That user has no copy
of your SSH keys, no access to your SSH agent socket, and no read access to
your home directory.

Linux:

```sh
sudo useradd --create-home --shell /bin/bash opsagent
sudo chmod 700 /home/opsagent
# Your own home must not be readable by others.
chmod 700 "$HOME"
```

macOS: create a standard (not administrator) user named `opsagent` in
**System Settings > Users & Groups**, then make sure your home folder is not
shared with it (`chmod 700 ~`).

Start the agent only from a fresh login shell of that user, without agent
forwarding and without your environment:

```sh
sudo -iu opsagent env -u SSH_AUTH_SOCK -u SSH_AGENT_PID bash -l
```

Check before the first session:

```sh
test -z "$SSH_AUTH_SOCK" && echo "no SSH agent"
ls ~/.ssh 2>/dev/null || echo "no SSH keys"
ls /home/<your user>/.ssh 2>&1 | grep -q "Permission denied" && echo "owner keys unreachable"
```

Do not add the agent user to `sudo`, `docker`, or other administrative groups,
and do not install your Git credential, cloud logins, or host keys for it.
Host work happens through Studio, not through your keys.

## 3. Start a session

### From the agent's terminal (recommended)

The terminal asks, you confirm in Studio with your second factor, and the
session goes straight into the agent user's protected file. Nothing secret
passes through your clipboard (Dossier
[elevated operator session](../../operator-elevated-session/index.html),
ES-3, decision ES-D2 option S2).

1. As the agent user, with [studio-op](studio-op.md) installed and
   `studio-op.json` pointing at your Studio:

   ```sh
   studio-op login --agent "ops console"
   ```

   It prints an eight-character code such as `WDJB-MJHT`, a link and a QR code
   of the link, and waits. `--label` changes the device label (default
   `<OS user>@<machine>`); `--class <id>` asks for an elevated action class
   (`cards.accept-override`, `cards.archive-override`, `project.testing`), which
   you can remove when you confirm; `--no-qr` leaves out the QR code.
2. Open the link on your computer or scan the QR code with your phone, or open
   **Confirm terminal sign-in** (`#/terminal-sign-in`) and type the code. Sign
   in first if Studio asks. Compare the code with the terminal and tick
   **This code is on my terminal now** when you came through the link.
3. Check what the page shows: the device label, **Known device** or **Unknown
   device**, the requesting address and whether it is on your browser's
   network, the operator agent, the action classes, and the lifetime. The
   default is 7 days, clamped to the allowed choices; memory keys allow at most
   24 hours. A shorter `--lifetime` request lowers the preselection. If you did not just run `studio-op login` yourself,
   choose **Deny**.
4. Enter your authenticator code, or leave the field empty to use a passkey,
   and choose **Confirm sign-in**.
5. Within a few seconds the terminal reports the session. `studio-op` wrote it
   to `~/.config/agent-studio/operator-session` (mode 600) and set
   `"credential": "operator-session"` in `studio-op.json`. It never prints the
   token. Check with `studio-op auth status`.

The code works once and for 10 minutes. Five wrong codes within 15 minutes
lock code entry for you for 15 minutes. Login starts a resident `studio-op agent`
under the agent's OS user and generates a fresh P-256 session key only in its
memory. Every command and event-stream connection signs through its protected
socket (mode 600 in a mode-700 directory), or a named pipe with a current-user-only
ACL on Windows. Windows uses Windows PowerShell for the pipe transport.
The key is never written to disk. Login removes the old ES-3
`operator-device-key.json`; existing terminal sessions require a new login.
`operator-session-agent.json` contains only the socket endpoint, token hash
and public thumbprint. Copying `operator-session` to another user or machine
gets 401 `dpop-required`; copying the metadata does not copy the key or socket.

Keep the resident running. It ends with failed sign-in, session expiry, a
signed 401, or within five seconds of detecting revocation or a removed or
replaced session file. After a restart or lost resident, run `studio-op login`
again; the private key cannot be recovered from a file. A denied second login
keeps the earlier session working. Each memory-key login shows as a new
device. Hardware hosts keep a persistent device identity and certify each fresh
memory session key at login (ES-5). Elevated action
classes stay recorded only until ES-7.

### Hardware device keys (ES-5)

Decisions ES-D3 B2 and ES-D4 A retain hardware providers under the operator's
26 November 2026 decision: **not yet verified on real hardware**, off by default
on every platform. The default is the tested resident software memory key.
To opt in, add `"hardwareDeviceKeys": { "win32": true }` to the agent user's
`~/.config/agent-studio/studio-op.json`. Use `darwin` for macOS or `linux` for
Linux; only the current platform's literal JSON `true` enables its provider.
Remove the setting or set it to `false` to return to the default.
Hardware labels in the product include **not yet verified on real hardware**. Login prints `Device binding: tpm`,
`secure-enclave` or `memory`; the confirmation page and session row show
**TPM key**, **Secure Enclave key** or **Memory key** in both themes. The
hardware key signs only the login certification. Request proofs continue to
use the resident's fresh memory key, so TPM latency does not affect commands.
The device key is reused for the same device and OS user. A hardware device
can be known across logins with different session keys. A failed certification
is unknown and memory-bound, even when the supplied hardware public key is known.

| Platform | Setup under the agent's OS user | Persistent state |
|---|---|---|
| Windows 10/11 | Enable TPM 2.0 in firmware. Windows PowerShell and .NET CNG must be available. Login uses the Microsoft Platform Crypto Provider, ECDSA P-256, signing-only and no export permission, with a current-user named key. It probes private export refusal before use. No administrator or machine-key flag is used. | CNG key `AgentStudio.OperatorDevice.P256.v1` in that user's platform key store. |
| macOS | Requires an available Secure Enclave and CryptoKit. Install Xcode Command Line Tools (`xcode-select --install`) to build the helper. In `tools/studio-op/native`, run `swiftc -O device-key.swift -o device-key-macos`, then `codesign --force --sign '<your stable signing identity>' device-key-macos`. Install the source and helper together with the CLI. Keep the same signing identity on upgrades; use a Developer ID identity for distribution. On a development machine, `codesign --force --sign - device-key-macos` permits local testing. Login does not compile or download a helper. | `device-key.enclave` in the agent's config directory, mode 600. This is an opaque hardware-wrapped blob; CryptoKit exposes no private scalar export. The helper also refuses a blob that can be imported as a software private scalar. |
| Linux | Install OpenSSL 3, `tpm2-openssl` and its `tpm2-tss` dependencies through the OS package manager (for example `apt install tpm2-openssl`). Give the agent user access to the TPM resource manager `/dev/tpmrm0`, typically through the `tss` group, then start a fresh user session. The OpenSSL `tpm2` provider must load and support P-256. A custom provider location uses OpenSSL's `OPENSSL_MODULES`; a managed TPM endpoint uses `TPM2OPENSSL_TCTI`. | `device-key.tss` in the config directory, mode 600. The provider generates a fixed-TPM, fixed-parent key under the TPM's storage hierarchy. The blob contains wrapped state, never a software private scalar. Login verifies that a conventional private-key export is refused. |

If hardware, permissions, the helper, the provider or signing is unavailable,
login prints a clear **Device binding: memory** note and continues with the
resident session key. It never substitutes a persistent software private key.
A missing or reset hardware key is a new device. Existing unusable wrapped
state is preserved, so temporary permission failures do not replace identity.
To intentionally replace it, first forget the device in Studio, end its
sessions, and remove the wrapped blob (or the named CNG key) as that OS user.

The [five-line manual probe instructions](../../operator-elevated-session/hardware-probe.md)
cover Windows TPM and macOS Secure Enclave checks. Run them only on the target
hardware; this delivery does not claim Windows or macOS platform execution.
The probe never silently accepts a memory fallback. For a copy check, copy only
the configuration directory to an independent machine, never its TPM state.

These signatures prove possession of the confirmed device key, not a
manufacturer attestation of the reported binding kind. Processes running as
the same OS user or root can still ask the platform key to sign. ES-6 now
adds hourly recertification and a chosen lifetime, described below. The
unbound Settings path retains its 1 to 8 hour limit.

Terminal sessions require fresh DPoP proofs, including a method, public URL,
token hash, timestamp within 60 seconds and single-use id. `studio-op` handles
these automatically. Typed 401 answers are `dpop-required`, `dpop-invalid`,
`dpop-replayed`, `dpop-stale` and `dpop-key-mismatch`; see the
[validation contract](../security/overview.md#proof-of-possession-binding-es-4).
Keep the host clock synchronized. Commands refuse redirects. Direct bearer-only
curl examples below
apply to the unbound Settings compatibility path; bound sessions must use
`studio-op` or a client that requests proofs from its resident signer.

### From Settings

Kept for unbound sessions without action classes until the migration slice retires it.
The session row explicitly says **Unbound**. Treat these files as bearer secrets.

1. In **Settings > Access > Operator agents**, choose **Start session** on the
   agent. Pick a name and a lifetime of at most 8 hours.
2. Studio shows the bearer once. Copy it into the agent user's protected file:

   ```sh
   umask 077
   mkdir -p ~/.config/agent-studio
   cat > ~/.config/agent-studio/operator-session   # paste, then Ctrl-D
   chmod 600 ~/.config/agent-studio/operator-session
   ```

3. Let the agent read the token from that file when it calls Studio, never
   from a repository, a prompt, or a chat message. The operator CLI does this
   for you: set `"credential": "operator-session"` in
   `~/.config/agent-studio/studio-op.json` and check with
   `studio-op auth status` ([studio-op setup](studio-op.md)). Do not expand the
   token into a command line (`$(cat ...)`): other local users can read process
   arguments.

## 4. Grant routine operations

1. Open **Settings > Access > Standing grants**. The operation catalogue at
   the top lists every operation by tier.
2. Under **Issue standing grant**, choose the agent, the scope (workspace,
   project, environment, or host), and the routine operations. A project scope
   cannot cover a host operation such as a service restart; the form says so.
3. Set the requests per hour, an optional daily window in UTC (for example
   22:00 to 06:00 for overnight work), the lifetime (7 days by default, at
   most 30), and the reason the agent needs it.
4. Enter an authenticator or recovery code, or leave the field empty to
   confirm with a passkey, and choose **Issue grant**. Each issue or renewal
   asks for a fresh second factor.

When the agent's request is refused, the answer names the reason:
`grant-required` (no live grant covers the operation in that scope),
`grant-outside-window`, or `grant-rate-exceeded`. **Revoke** takes effect on
the agent's next request and needs no second factor. Disabling the agent
revokes its grants too. The methods, routes, bodies, and refusal codes behind
this page are listed in the
[security overview](../security/overview.md#operator-operation-catalogue-tiers-and-standing-grants-sm-4).

## 5. Approve high-risk requests

A high-risk (T2) request of the agent opens an approval request instead of
running. The agent gets 403 `approval-required` with an `approvalId`.

1. Let the agent say where the instruction came from and why, on the T2
   request itself:

   ```sh
   curl -sS -X DELETE \
     -H "Authorization: Bearer $(cat ~/.config/agent-studio/operator-session)" \
     -H "X-Operator-Instruction-Source: owner-chat" \
     -H "X-Operator-Reason: Remove the duplicate card" \
     "https://studio.example.test/api/tasks/AGT-12?project=PROJ-001"
   ```

   The source is one of `owner-chat`, `card`, `repository-file`, `web-page`,
   or `log`; anything else is shown to you as not stated. Percent-encode a
   reason that is not plain ASCII.
2. Open **Settings > Access > Approval requests** (the operator feed notice
   and the optional push notification link there). Each request shows the
   operation, the request line, the parameters with secret-like values
   masked, the parameter digest, the target, the session, the instruction
   source, and the agent's reason. A source other than your own chat is
   marked as untrusted content: it can propose, never authorize.
3. Enter an authenticator or recovery code, or leave the field empty to use a
   passkey, and choose **Approve** or **Reject**. Each decision asks for a
   fresh second factor. You have 15 minutes from the request.
4. The agent repeats the exact request with `X-Operator-Approval: <approvalId>`
   within 15 minutes of your approval. It runs once; the result is recorded
   against the approval. A replay answers `approval-used`, a changed card,
   query, or body `approval-digest-mismatch`, a late attempt
   `approval-expired`, and a request still waiting `approval-pending`.

### Overrides: accept or archive a card with a written reason

An override acceptance (a move to Completed with `operatorOverride`) and an
archive override (a move to Archive with `archiveOverride`) need the
permission `project.cards.override`, which only Owner and Admin hold. The
agent's request is the T2 operation `card.accept-override` or
`card.archive-override` and opens an approval request like any T2 request.
When you approve it, the agent's replay of that exact request carries the
permission for that one request and the card moves:

```sh
curl -sS -X POST \
  -H "Authorization: Bearer $(cat ~/.config/agent-studio/operator-session)" \
  -H "Content-Type: application/json" \
  -H "X-Operator-Instruction-Source: owner-chat" \
  -H "X-Operator-Approval: <approvalId>" \
  -d '{"targetState":"6-completed","operatorOverride":true,"reason":"Delivered outside Studio, verified on stage."}' \
  "https://studio.example.test/api/tasks/AGT-12/move?project=PROJ-001"
```

An archive override uses the same route with
`{"targetState":"7-archive","archiveOverride":true,"reason":"..."}`; the
approval request shows it as `card.archive-override`. `PUT
/api/tasks/{id}/state` takes the same body and is classified the same way.

The `reason` field is mandatory (at least eight characters). Without it the
agent gets the 400 reason error at once and no approval request opens. A plain
move to Archive without `archiveOverride` is not in the catalogue and is
refused with `operation-not-catalogued`. Each card needs its own approval
until batch confirmation arrives (ES-8). The card timeline names the session
as actor, you as `approvedBy`, and the reason.

What the agent gets for an override:

| Situation | Answer |
|---|---|
| No `reason`, or one shorter than eight characters | 400 with the reason error; no approval request |
| Valid request, no approval yet | 403 `approval-required` with `approvalId` and the operation |
| Replay with your approval | 200; the card moves once |
| Second replay, changed body or card, late replay | 403 `approval-used`, `approval-digest-mismatch`, `approval-expired` |

The approval adds `project.cards.override` to the approved request only.
The agent does not keep it, and no approval of another operation grants it.

The approval is bound to the session that asked; another session gets
`approval-not-found`. Revoking the session or disabling the agent cancels its
open approvals. Nothing the agent sends names who approved: Studio records
your session's user.

Optional push notification: set the address Studio is reached at and an
HTTPS endpoint that forwards JSON (`approvalId`, `title`, `message`, `url`)
to your phone, for example a self-hosted notification relay:

```sh
OperatorApprovals__StudioUrl=https://studio.example.test
OperatorApprovals__Push__Url=https://push-relay.example.test/studio-approvals
OperatorApprovals__Push__TokenFile=/run/secrets/approval-push-token
```

The notification only links to the request; opening it still needs your
Studio sign-in and a second factor to decide. The routes, fields, and
refusal codes are listed in the
[security overview](../security/overview.md#owner-approval-with-step-up-sm-5).

## 6. Renew, end, and review

- **Renew** applies to an unbound Settings session and extends it to at most
  8 hours from now. Bound sessions automatically rotate their certified key
  without extending the chosen end; changing that end requires a new sign-in.
- **End session** ends one session at once and leaves the agent and its other
  sessions working; **Disable** ends the agent and every session. A session
  also ends when your Owner role ends.
- A session from a terminal sign-in shows its binding kind (hardware kinds include
  **not yet verified on real hardware**), device label, key thumbprint and
  the terminal's address at confirmation in the session row. **Known devices**
  under the agent lists each confirmed device key with its last confirmation
  and last use; **Forget device** ends its sessions at once, and its next
  sign-in shows as unknown.
- A terminal sign-in session also ends when you revoke the browser session
  that confirmed it in a session list, when your password is reset or you
  change it, and when your account is disabled. Signing out of that browser,
  or its session expiring, does not end it.
- After a session, delete the token file:
  `rm ~/.config/agent-studio/operator-session`.
- **Settings > Access > Operator activity** lists each session with its
  entries, the result of the last chain verification, and how far entries have
  been forwarded to the off-host audit sink. **Verify now** recomputes the
  chain and compares it with the sink.

## 7. Kill switch

**Settings > Access > Kill switch > End all operator access** disables every
operator agent, ends every session, revokes every standing grant, cancels
every open approval request, revokes their Operations Server principals, and
cancels their queued host operations (security model SM-2). **End session** on
one session row ends that session and cancels its open approval requests.
Every operator call is refused within 60 seconds; in practice the next Studio
request already gets `401`. To resume, create a new operator agent and start a
session. Each use appears in the audit trail with its own event type
(`operator-kill.all`, `operator-kill.session`, `operator-kill.drill`) and as
an operator feed event. The Kill switch panel lists each use with the agents,
sessions, standing grants and approval requests it ended.

### When the Studio UI is unreachable

Run the command on the control plane host. It refuses every operator session
from the moment it starts, also while the API is running, and writes the same
audit event as the Studio action.

Website stack (`deploy/compose/monolith-networked/`):

```sh
cd deploy/compose/monolith-networked
./end-operator-access.sh --reason "Studio unreachable"
```

Any other installation, from the published API folder with the same
configuration the API uses:

```sh
dotnet OrchestratorApi.dll --end-operator-access --reason "Studio unreachable" \
  --TaskRepository=/srv/agent-studio/workspace
```

Exit code `0` means the kill is applied and, when an Operations Server is
configured, its operator principals are revoked. `2` means Studio access is
ended but the Operations Server could not be reached; the API retries when it
runs, and on the Operations Server host you can run the same revocation
directly:

```sh
operations-server revoke-operators /run/operations/principals/principals.json /var/lib/operations
```

`3` means the API is running but did not confirm within the wait time; the
latch keeps refusing every session until it does.

### Operations Server principals

An Operations Server principal that the operator agent uses names the agent in
`operatorAgentId`, for example:

```json
{ "id": "operator-console", "audience": "operations-server", "kind": "service",
  "tokenSha256": "<sha256 of the bearer>", "scopes": ["host.inspect", "operations.read"],
  "agentIds": ["<host agent id>"], "roots": [], "operatorAgentId": "opa_<id>" }
```

Studio revokes these with its own control plane principal, which holds only
the scope `principals.revoke`. The Operations Server records each revocation
in `operator-revocations.json` in its data directory and never edits the
principal file, which can stay mounted read-only:

```sh
Security__OperatorKillSwitch__OperationsServer__Url=https://operations.example.test
Security__OperatorKillSwitch__OperationsServer__CredentialFile=/run/secrets/operations-control-plane
```

### Drills

A drill exercises the switch against a test principal and records the
measured time; it never touches a real session. Studio runs one every 30 days
(`Security__OperatorKillSwitch__DrillIntervalHours`, `0` turns the schedule
off), and **Run drill now** runs one on request. The Kill switch panel shows
the last drill with its duration; a failed drill is an acute operator feed
event that names the reason, for example `operations-server-unreachable`.

## 8. Host access migration (SM-6)

1. Deploy the networked stack with the published Operations Server and
   Operations Agent image digests. Its bootstrap creates independent service
   and agent principals on protected volumes. Keep port 5081 bound to
   loopback and give any remote Operations Agent a private authenticated
   tunnel to it. Set `Operations:Agents:<host id>` in Studio to the enrolled
   agent id. The Compose `local` agent advertises inspection only until a
   host allowlist is mounted.
2. On each host, install an Operations Agent under its own
   `agent-operations-service` identity, separate from the SSH account.
   Supply `OPERATIONS_URL`, `OPERATIONS_TOKEN_FILE`, `OPERATIONS_AGENT_ID`,
   `OPERATIONS_SPOOL_DIRECTORY`, and an administrator-owned,
   service-readable `OPERATIONS_HOST_OPERATIONS_FILE` (mode 0640, group
   `agent-operations-service`). Its JSON fields are
   `agentHostExecutable`, `hostAgentConfig`, `logSources` (name to absolute
   file), `configurations` (name to `sourceFile`, `targetFile`, and reviewed
   `sha256`), and `revokedKeysFile`. The source and target paths live in the
   host's allowlist, never in an operator request. Scope the agent principal
   to this agent id and only the operations it advertises. Run the SSH setup
   in step 3 before starting the agent so its service-owned KRL directory
   exists.
3. Give the CA public key from `STUDIO_HOST_CA_DIR/ca.pub` to the host
   administrator. Run `deploy/agent-host/setup-operations-ssh.sh <ca.pub>` as
   root. It creates the dedicated unprivileged `agent-operations` account,
   removes every standing key from that account's `authorized_keys`, installs
   the forced command and SSHD restrictions, and reloads SSHD only after
   `sshd -t` succeeds. The SSH account cannot write the KRL; only the
   separate agent service identity can. Set `revokedKeysFile` to
   `/var/lib/agent-operations/revoked.krl` and verify that the first KRL has
   reached the host before trying a certificate. For any older operator key
   installed in other accounts, put its reviewed `ssh-keygen -lf` fingerprint
   in an owner-only file and run
   `deploy/agent-host/remove-operator-keys.sh <fingerprints-file> <authorized_keys-file>...`
   as root with each affected file. Confirm those fingerprints no longer
   appear on every host. Keep any emergency credential in separate break-glass
   custody, outside the operator account and agent home.
4. Grant the operator only the needed host T1 operations in Settings > Access.
   Call a typed Studio host route with an `idempotencyKey` and its small
   `input` object. For example, a review-role drain sends
   `{ "idempotencyKey": "one-unique-request-id", "input": { "role": "review" } }`
   to `POST /api/hosts/{id}/drain`. Poll
   `GET /api/hosts/{id}/operations/{attemptId}` for the result. The response
   includes the central attempt id and transition history; the host journal
   lives beside the agent spool as `<spool>.journal.jsonl`.
5. For an uncovered command in the fixed host command allowlist, generate a
   fresh SSH key pair in the operator's own OS account, send its public key
   to `POST /api/hosts/{id}/certificates`, and use the returned certificate
   only with that key. The endpoint requires an active operator session and
   a live host grant. Revoke with
   `POST /api/hosts/{id}/certificates/{certificateId}/revoke`; wait for the
   agent to install the new KRL, then verify OpenSSH refuses the certificate.
   Certificates expire within 60 minutes even without revocation.

Keep the CA private key and the operator's ephemeral private key separate.
The CA private key is a Studio host secret and is absent from host agents.
The operator account has no Owner SSH agent, keys, or credential files.
The website backup archive does not include the Operations Server named
volumes or the CA directory outside `STUDIO_DATA_DIR`. Preserve the volumes
and an offline CA key copy during an in-place host recovery. On a new host,
the scheduled backup restore re-bootstraps Operations Server and requires
remote host agents to be re-enrolled; see the
[restore runbook](./studio-backup-restore.md#restore-onto-a-new-host).

## Off-host audit sink

Configure the sink on the Studio host so the session's history survives a
takeover of that host (see
[Control plane audit chain](../security/overview.md#control-plane-audit-chain-security-model-sm-1)):

```sh
ControlPlaneAudit__Sink__Kind=https
ControlPlaneAudit__Sink__Url=https://audit-sink.example.test/studio
ControlPlaneAudit__Sink__AppendTokenFile=/run/secrets/audit-sink-append
```

The append credential must only be able to add entries. Without a sink the
chain is still verified locally and the activity view says that no sink is
configured.

## Break-glass custody and host recovery

In **Settings > Access > Break-glass windows**, enter a fresh authenticator
code or use a passkey and choose **Issue sealed credential**. Studio shows the
random value once. Seal it in offline Owner custody, away from the operator
agent's OS user, host credential files, and the repository. Opening consumes
the credential; issue and seal a replacement after each use. The Owner then
enters the credential, another fresh second factor, the exact operator session
IDs, a project ID or short code for the review card, and one or two hours.
The window ends automatically at its deadline or when the Owner closes it.
Any host certificate issued through a named session during the window expires
no later than that deadline. Stop using a certificate when the Owner closes
the window early; the Owner should revoke it through Studio so hosts install
the updated revocation list.

If Studio is stopped, run the host command from the control-plane host under
the administrative account that can read the security state. It requires a
configured off-host audit sink. The command reads the sealed credential and
current TOTP code from the terminal without echo and never accepts either as
a command argument:

```sh
dotnet OrchestratorApi.dll --open-break-glass \
  --owner '<owner username>' --project '<project id>' \
  --sessions '<operator session id>' --hours 2
```

The host command writes the opening entry to the separate host journal and
forwards it to the sink. On recovery, Studio alerts on the window, closes it
when due, and creates a Human review card listing the break-glass audit entries.
The reviewer compares the card with the host journal and sink. Revoke the
named sessions with the kill switch if any action must stop sooner.

While Studio remains stopped, record each host action with its operation,
target, and outcome. The command requires the current authenticator code on
hidden input and confirms the host journal and sink acknowledgement. Each
opening and recorded action needs a code from a later 30-second step than the
last accepted code:

```sh
dotnet OrchestratorApi.dll --record-break-glass \
  --owner '<owner username>' --window '<window id>' \
  --operation 'service.restart' --target '<allowlisted service>' --outcome 'ok'
```

## Testing strategy as a project setting (ES-1)

`GET /api/projects/{project}/testing` requires `project.read`.
`PUT /api/projects/{project}/testing` requires `project.settings.write`, held
by the Owner and Admins of that project. Developers and Viewers cannot change
it. Project settings shows the strategy control to project Admins as well as
the Owner. A successful change still rebuilds queued review plans and uses the
asynchronous batch-gate handoff. The response transition and audit share the
old and new effective strategies captured under the settings mutation lock,
including when the requested strategy already matches.
Switching to post-merge testing stops pending full gates and cancels running
fallback gates without recording a failed test verdict. The handoff records
integration evidence separately from a full-suite pass.

For operator sessions, the catalogue operation `project.testing.set` is T2
with project scope. A session with read access to the project can request an
SM-5 approval for the exact strategy request. After the Owner approves with
step-up, repeat the same request with `X-Operator-Approval`; it runs once.
Send `X-Operator-Reason` with the stated reason, percent-encoded when needed.
The operator-agent role does not hold `project.settings.write`; this narrow
approval path does not grant that permission to other requests. The armed
`project.testing` class can also admit this request, as described below.

Each strategy change writes one `project.testing.set` control plane audit
entry with actor, project, old and new strategy, and the stated reason for an
operator session. The settings service captures both values under its private
mutation lock, so concurrent writers cannot change the values used by another
request's audit entry. Operator attribution includes the session and delegating
Owner. Build profile, review package, security file, checkout and gate
capacity routes retain their existing authorization.

## Choose a lifetime and renew the device binding (ES-6)

Run `studio-op login --agent '<name>' --lifetime 24h` to ask for a shorter
preselection. Accepted values are `8h`, `24h`, `7d` and `30d`. The browser
preselects the installation default or your shorter request, clamped to the
agent and device limits; a longer terminal request cannot increase the default.
The Owner may choose any offered lifetime with a fresh second factor.
The default is 7 days, but a memory-key host can receive only 8 or 24 hours.
Until revoked requires a hardware-bound device and an Owner limit that allows
it. ES-5 hardware keys remain opt-in and not yet verified on real hardware.

The resident `studio-op agent` keeps both the device signing capability and
current session key available. It rotates the session key ten minutes before
its 60-minute certification ends, using a device-signed renewal and a current
DPoP proof. No authenticator code or passkey prompt appears during renewal.
The previous session key stops working immediately after rotation. Do not
copy the configuration to another host: it has neither the session key nor
the device key needed to renew. A missing or forgotten key, stopped resident,
expired chosen or idle end, disabled confirming person, missing second factor,
kill switch or lowered limit requires `studio-op login` again. The CLI reports
that instruction after renewal refusal; the resident writes the same quiet
instruction to `operator-session-ended` beside the session file.

`studio-op auth status` shows chosen end, idle end and binding kind for an
operator session. The resident's health checks and renewal do not count as
use. Other authenticated requests reset the idle clock. A session left unused
ends after 24 hours by default even if the resident keeps renewing it.
Signing out of the confirming browser, or letting that browser session expire,
does not end the terminal session. Revoking that browser session explicitly,
changing or resetting the confirming person's password, disabling them or
removing their last second factor does end it.

Use Settings > Access > Session limits to change the default, role limits,
idle limit or an agent's additional limit with a fresh Owner step-up. Every
change is audited. A lower limit ends an older session on its next renewal;
a higher limit does not extend an already confirmed session. The Settings
bearer path remains limited to 1 to 8 hours. To change a bound session's chosen
end, sign in again and confirm a new session.

Settings > Access > Elevated sessions lists long sessions and sessions with
classes. End session, End elevation, Forget device and End all elevated
sessions are one-tap actions without step-up. End elevation removes classes
and keeps ordinary T0 and T1 access. A new address at renewal marks the row
quietly. The security view's 30-day notice offers Keep or End; Keep schedules
another review in 30 days and does not extend the session. Ending applies on
the next request and live operator connections recheck within 15 seconds.

After upgrading from a bound session without hourly certification, run a new
`studio-op login`: Studio accepts that old key for at most 60 minutes from
its confirmation. The kill switch also invalidates uncollected sign-in codes.

## Use elevated classes and re-arm testing (ES-7)

At terminal confirmation leave only the requested classes you want the
session to carry. The implemented choices are ES-D6 A, ES-D9 A and ES-D16 A.
`project.testing` lets the session call `PUT /api/projects/{project}/testing`
without a separate approval for 24 hours from confirmation. Send a nonempty
`X-Operator-Reason`; `X-Operator-Instruction-Source` records the source as the
agent states it. Your project bindings still apply. There is no 60-minute
elevation window, and the session's chosen end always wins.

The header's Elevated control opens the session controls on desktop and
phone. On phones it sits above the existing header actions to keep it reachable.
Its Settings link opens and focuses the Elevated sessions list after it loads.
Settings > Access > Elevated sessions shows classes, hourly uses by
project and remaining arming time. In the last hour a quiet reminder offers
Re-arm. Choose **Re-arm project.testing**, then enter an authenticator or
recovery code, or leave it empty for a passkey. Only an Owner can confirm.
That grants another 24 hours, never past the session end. The agent cannot
re-arm, add a class, or extend your session. Re-arming keeps rate history.

Each direct change appears in the project Activity feed with **Undo**.
Undo restores the previous strategy as you, with an audit entry, once. It
also performs the normal queued review replanning and testing-mode handoff.
The agent cannot invoke it. Settings > Access > Operator activity filters
by agent, session or **Elevated actions only** and shows class, reason,
stated source, device thumbprint and arming id.

Six testing requests per project per rolling hour are allowed; the seventh
returns `elevation-rate-exceeded`. Admitted requests consume a slot even if
invalid parameters fail later. Three rate refusals within ten minutes end all
classes and add a Needs you row. Ordinary session access stays available.
Review the activity, end or acknowledge the elevation, and sign in again if
you need new classes. Without the class, or after its arming lapses, testing
returns `approval-required` and you can use the ordinary exact-request
approval flow.

The two override classes last for the session: `cards.accept-override` has a
30-item hourly limit and `cards.archive-override` a 10-item limit. Their batch
confirmation is ES-8; until then each override still needs its own approval.
End elevation removes all classes without a second factor, End session ends
the session, and the kill switch ends every elevation and session. Secrets,
production deployment and approval, access and session management, grants,
break-glass management, catalogue, audit and installation administration
remain hard refusals for the agent regardless of class or approval.
<!-- append-only:operator-agent:end -->

## Confirm a batch in Needs you (ES-8)

Request `cards.accept-override` or `cards.archive-override` at terminal sign-in.
Prepare a JSON array with up to 25 items. Each item has `card`, `project` and
`reason`; acceptances also have `reservation`, the review's concern in one line.
For example:

```json
[
  { "card": "DEMO-12", "project": "Demo", "reason": "Accept the reviewed naming tradeoff", "reservation": "One helper name remains broad" }
]
```

Run `studio-op accept --batch items.json --source owner-chat` or
`studio-op archive --batch items.json --source owner-chat`. The CLI prints the
confirmation link and waits for Studio's per-item results. It never replays
moves. The low-level routes are `POST /api/auth/operator-session/batches` and
`GET /api/auth/operator-session/batches/{id}`; both require a live operator
session and, for a bound session, its device proof.

Open Needs you, then the operator approval row. The phone-sized sheet shows the
card key and title, recorded reservation, agent's reason and card link. Card links open in another tab and preserve the selected set. Items
that fail qualification are disabled with their reason. Qualifying items start selected. Deselect any others,
then confirm once with a passkey or authenticator code. Acceptances must still
be in Human review with an integrated delivery and a review that ran; an archive
override requires a completed card with no delivery on a protected integration or release branch. Existing
completion and archive guards also apply. Studio runs selected items in order
as the operator session, recording the Owner approver and batch id on each
card's timeline. Operator activity also lists each item under elevated actions
with its class, batch id, device, reason and result. Changed cards and refused
items are skipped with a result.

A batch lapses 30 minutes after submission and can be decided only once.
Rejection and lapse execute nothing. The Needs you approval count includes
single SM-5 approvals as well as batches, and clears when they stop waiting.
Feed and optional push notifications link to the same sheet and grant no
authority. Hourly limits count individual items: 30 acceptances or 10 archives.
If execution is interrupted, inspect the batch result and card timelines before
submitting another batch; Studio will not replay the original confirmation.

Batch submission accepts at most 64 KiB and 20 pending batches per session.
Stored batches older than 30 days are pruned when another batch is submitted;
card timelines and security audit records retain their existing retention policy.
