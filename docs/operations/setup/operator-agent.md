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
| You, the Owner | Creating the agent, starting, renewing, and ending sessions; the kill switch | |

Every request the session makes, every audit entry, and every card timeline
event it causes names `operator-agent:<id>/session:<sid>` as actor and
`human:<your user id>` as `delegatedBy`. A session token cannot call access
administration, read or verify the audit trail, create role bindings, resolve
secret values, or use the Task Server plane; Studio answers 403.

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

## 4. Renew, end, and review

- **Renew** extends an active session to at most 8 hours from now. Only you
  can renew; the agent cannot.
- **End session** ends one session at once and leaves the agent and its other
  sessions working; **Disable** ends the agent and every session. A session
  also ends when your Owner role ends.
- After a session, delete the token file:
  `rm ~/.config/agent-studio/operator-session`.
- **Settings > Access > Operator activity** lists each session with its
  entries, the result of the last chain verification, and how far entries have
  been forwarded to the off-host audit sink. **Verify now** recomputes the
  chain and compares it with the sink.

## 5. Kill switch

**Settings > Access > Kill switch > End all operator access** disables every
operator agent, ends every session, revokes their Operations Server
principals, and cancels their queued host operations (security model SM-2).
Every operator call is refused within 60 seconds; in practice the next Studio
request already gets `401`. To resume, create a new operator agent and start a
session. Each use appears in the audit trail with its own event type
(`operator-kill.all`, `operator-kill.session`, `operator-kill.drill`) and as
an operator feed event.

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
