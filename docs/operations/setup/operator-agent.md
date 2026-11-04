# Operator agent

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
| Operator session | One `oas.*` bearer in the agent user's protected file | 1 to 8 hours, renewed only by you |
| Standing grant | The routine (T1) operations the agent may run, by scope, rate, and window | 7 days by default, at most 30, renewed only by you |
| Approval | One exact high-risk (T2) request you approved after a second factor | Used once, within 15 minutes of your decision |
| You, the Owner | Creating the agent, starting, renewing, and revoking sessions and grants | |

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
   projects it operates is a typical start. Owner cannot be bound.

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
   from a repository, a prompt, or a chat message. For example:

   ```sh
   curl -fsS -H "Authorization: Bearer $(cat ~/.config/agent-studio/operator-session)" \
     https://studio.example.test/api/tasks
   ```

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

- **Renew** extends an active session to at most 8 hours from now. Only you
  can renew; the agent cannot.
- **Revoke** ends one session at once; **Disable** ends the agent and every
  session. A session also ends when your Owner role ends.
- After a session, delete the token file:
  `rm ~/.config/agent-studio/operator-session`.
- **Settings > Access > Operator activity** lists each session with its
  entries, the result of the last chain verification, and how far entries have
  been forwarded to the off-host audit sink. **Verify now** recomputes the
  chain and compares it with the sink.

## Host access migration (SM-6)

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
volumes or the CA directory outside `STUDIO_DATA_DIR`. Preserve those volumes
and an offline CA key copy as part of host recovery; restore the matching
principal and token volumes together before starting Operations Server.

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
