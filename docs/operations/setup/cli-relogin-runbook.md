# Runbook: renew provider authentication on an execution host

Use this runbook when an Execution Hosts provider badge changes from **OK** to
**Unavailable**, a Ready card says it is waiting for a provider sign-in, or a
run reports `ProviderUnauthorized`.

The authoritative provider session is the native Claude or Codex credential
store in the runner user's home directory. A provider probe runs the CLI's own
status command as that user. It may read expiry and modification timestamps
from the same host-owned files, but never returns or logs token values.

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
- **Logged out**: explicit provider-login output confirmed that the
  account is genuinely signed out. Hover for detail such as `Not logged in`.
- **Expired**: the latest known credential expiry is in the past.
- **Unavailable**: the probe ran but could not establish a usable provider
  session for another non-transient reason.
- **Unknown**: no current provider-auth advertisement exists, the advertisement
  is stale, or the runner is unreachable.

Provider transitions are retained in the capability recovery history. An
`OK -> Logged out` transition creates an operator notification after an
explicit login failure. Tool errors, generic exit 1 output,
timeouts, and rate limits never create a sign-in notification. A later positive
probe clears the matching provider-auth circuit and re-advertises recovery
without a runner restart. Ready cards show sign-in blocking only for the
confirmed logged-out or expired state.

If the runner advertises a credential expiry, Studio warns once when it enters
the final 14 days. An absent expiry is reported as unknown and is never guessed
from the secret.

## 2. Renew through Studio

1. Open the affected host in **Execution Hosts**, expand **Capabilities**, and
   choose **Re-authenticate Claude** or **Re-authenticate Codex**.
2. Open the displayed HTTPS URL and enter the one-time code when one is shown.
3. Keep the dialog open while the CLI stores its session in the remote runner
   user's native credential directory.
4. Wait for the dialog to observe a newer **OK** provider probe.

Studio retains only the short-lived browser instructions while the flow is
pending. It does not receive or persist the resulting token, and it never puts
a credential in an SSH argument, task, log, repository, or result artifact.
The remote CLI owns the credential file and refreshes its own session.

## 3. Verify recovery

The next provider probe arrives within five minutes. Recovery is complete when:

1. The host badge is **OK** and its observed timestamp is newer than the login.
2. Recovery history records the unavailable-to-ready transition.
3. Ready cards no longer show a provider sign-in wait reason.
4. A normal probe card can be claimed and completed on that host.

A card already in Human Review is not silently treated as a product failure.
The review infrastructure path requeues it under the existing retry budget.

If the login succeeds but no newer advertisement arrives, inspect the Coding
and Review units and runner journal:

```bash
ssh agent-runner-01 'claude auth status --text && codex login status'
sudo systemctl is-active agent-host.service agent-runner-review.service
sudo journalctl -u agent-host.service -u agent-runner-review.service -n 100 --no-pager
```
## 4. Prohibited recovery paths

- Do not create a provider-specific `claude.env` or shared provider
  EnvironmentFile. Native host login is the authentication source.
- Do not copy `~/.claude/.credentials.json`, `~/.codex/auth.json`, or any other
  workstation credential file to the host.
- Do not put a token or setup token on an SSH command line, in a task prompt, or in a card.
- Do not edit the Studio database or repository to distribute a credential.
- Do not treat a systemd restart alone as renewal. Recovery requires a fresh
  provider-auth probe.
