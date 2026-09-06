# Runbook: renew provider authentication on an execution host

Use this runbook when an Execution Hosts provider badge changes from **OK** to
**Unavailable**, a Ready card says it is waiting for a provider sign-in, or a
run reports `ProviderUnauthorized`.

The authoritative store for environment-backed provider credentials is
`/etc/agent-runner/provider-auth.env`, owned by `root:agent` with mode `640`.
Codex browser authentication is stored separately in the runner user's native
Codex credential store. Both are host-owned. A provider probe uses the daemon
process environment and CLI status as authentication authority. It may also
read expiry and modification timestamps from native Claude and Codex credential
files, but never returns or logs token values.

## 1. Confirm the affected provider and host

Open **Workspace Settings > Execution Hosts** and inspect **Provider
authentication** on the affected host. The badge exposes these states:

- **OK**: a fresh capability snapshot reports usable provider authentication.
- **Retrying**: a timeout, network error, or token-refresh race occurred. The
  last-good capability remains usable while the probe retries.
- **Limited**: the provider rejected work at an account limit. Matching claims
  wait until the displayed reset time and resume after recovery is confirmed.
- **Expiring**: the CLI still confirms an active session, but credential
  metadata is inside the 14-day warning window.
- **Unavailable**: repeated explicit provider-login output confirmed that the
  account is genuinely signed out. Hover for detail such as `Not logged in`.
- **Unknown**: no current provider-auth advertisement exists, the advertisement
  is stale, or the runner is unreachable.

Provider transitions are retained in the capability recovery history. An
`OK -> Unavailable` transition creates an operator notification only after two
consecutive explicit login failures. Tool errors, generic exit 1 output,
timeouts, and rate limits never create a sign-in notification. A later positive
probe clears the matching provider-auth circuit and re-advertises recovery
without a runner restart. Ready cards show sign-in blocking only for the
confirmed unavailable state.

If the runner advertises a credential expiry, Studio warns once when it enters
the final 14 days. An absent expiry is reported as unknown and is never guessed
from the secret.

## 2. Renew Codex through Studio

1. On an **Unavailable** or **Expiring** Codex badge, choose **Sign in Codex**.
   The same action is available on a Codex Ready-card wait chip.
2. Open the displayed verification link in a browser and enter the large
   one-time code.
3. Leave the dialog open while it polls the session handle. Codex stores the
   resulting credential on the execution host, not in Studio.
4. Wait for the dialog to close after a fresh `provider-auth:codex` probe reports
   **OK**. Studio best-effort restarts installed runner units after
   `codex login status` succeeds so that probe is published promptly.

The remote login process is killed after 15 minutes. Studio keeps no token or
auth file and discards the displayed URL and code at terminal completion. One
`provider_sign_in` operator-feed event records only host, provider, actor, and
outcome. If the browser flow fails, choose **Try again**; do not fall back to
copying `~/.codex/auth.json` from another machine.
If unit restart is unavailable, the ordinary provider-probe cadence remains the
fallback.

## 3. Renew Claude through Studio

1. Open the affected host in **Execution Hosts** and choose **Set up agent
   host**.
2. In **Provider authentication**, select `CLAUDE_CODE_OAUTH_TOKEN` or
   `ANTHROPIC_API_KEY` and paste the replacement value.
3. Choose **Provision and verify**.
4. Wait for **Latest runner probe: OK**. The dialog clears the input after both
   successful and failed requests.

Studio sends the value to the selected host through SSH stdin. It never places
the value on the SSH command line, in shell history, in the setup task, in the
Studio database, in logs, in the repository, or in `results/`. The host
atomically updates the shared file, restarts the installed Coding and Review
units, verifies only the variable name in `/proc/<MainPID>/environ`, and waits
for a newer runner probe.

Provisioning one Claude credential replaces the other Claude variable while
preserving entries for future providers in the shared file.

## 4. Verify recovery

The next provider probe normally arrives within 60 seconds. Recovery is
complete when all of these statements are true:

1. The host badge is **OK** and its timestamp is fresh.
2. The newest recovery-history entry records `unavailable -> ready`.
3. Ready cards no longer show the provider sign-in wait reason.
4. A normal probe card can be claimed and completed on that host.

A card that already reached Human Review after an auth failure is not moved
automatically. Requeue it only after the host badge is **OK**.

If the EnvironmentFile installation succeeds but no new advertisement arrives,
inspect the Coding and Review unit state and the runner journal. Do not paste the
credential into a diagnostic command. The safe host-side checks are:

```bash
sudo stat -c '%U:%G %a %n' /etc/agent-runner/provider-auth.env
sudo systemctl is-active agent-host.service agent-runner-review.service
sudo journalctl -u agent-host.service -u agent-runner-review.service -n 100 --no-pager
```

## 5. Prohibited recovery paths

- Do not create a provider-specific `claude.env` file. There is exactly one
  provider EnvironmentFile.
- Do not copy `~/.claude/.credentials.json`, `~/.codex/auth.json`, or any other
  workstation credential file to the host.
- Do not put a token on an SSH command line, in a task prompt, or in a card.
- Do not edit the Studio database or repository to distribute a credential.
- Do not treat a systemd restart alone as renewal. Recovery requires a fresh
  provider-auth probe.
