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
bypassing a gate, are refused with `approval-required`. Anything outside the
operation catalogue is refused with `operation-not-catalogued`. A chat message
or the agent's memory never grants anything; only grant records do.

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

## 5. Renew, end, and review

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
