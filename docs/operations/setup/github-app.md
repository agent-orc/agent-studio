# GitHub App

One GitHub App gives every host short-lived tokens, so nobody creates, copies or
renews personal access tokens. Connect it under **Settings > GitHub**.

## What you get

| Who | What it holds | Lifetime |
|---|---|---|
| Studio or Task Server | The App's private key and client secret, in the secret provider or the encrypted root store | Until rotated |
| Studio host Git | A write token per registered repository, in a protected file outside the workspace repository | One hour |
| Coding and integration hosts | A write token per registered repository | One hour |
| Review hosts | A read token per registered repository | One hour |

A host asks again every five minutes and receives a token with at least 25
minutes left. Tokens are limited to one repository and to `contents`, `metadata`
and, for write tokens, `workflows`. Without `workflows`, a delivery that touches
`.github/workflows` cannot be pushed.

The gate needs no token: it receives its source as a bundle.

## Connect

1. Open **Settings > GitHub** and choose **Connect GitHub**.
2. GitHub shows the App it will create. Confirm it; the private key goes straight
   into the secret store. Leave **Installable on other accounts** on when your
   repositories live under more than one account: a private App can only be
   installed on the account that owns it.
3. Choose the accounts and repositories. To add one later, use **Add repositories
   or an account**.
4. Authorize the App with your GitHub account. This proves the installation is
   really yours, because the address GitHub redirects to could be called by
   anyone with a forged installation number. The user token this produces is
   revoked immediately.

Tokens are only ever issued for an installation confirmed this way, so somebody
else installing a public App gains nothing.

**Use an App that already exists** takes the App ID, client ID, client secret and
private key instead of step 2. Set the App's callback URL and setup URL to this
Studio's `/github-app/callback`.

A Studio that moves to another address needs the new address added to the App's
callback URLs on GitHub.

## Give a host access in the local profile

The local profile has no sign-in, and the hosts reach its API through their SSH
tunnels. A key therefore cannot be issued over that API. On the Studio machine:

```sh
scripts/github-app-host-key.sh issue <workspace-repository> <runner-id> write > key
```

Use `read` for a review host. Copy the key to a file only the host's service user
can read, name that file in `RUNNER_REPOSITORY_KEY_FILE`, and restart the host
service. The Studio stores only the key's SHA-256 under
`.security/github-app/host-keys/`. To revoke a host, run `revoke`; it loses its
tokens at its next sync.

## Give a host access in a networked profile

Hosts present their service credential, so no key is needed.

- Task Server: enrolment decides. A host-join token with the role `coding` grants
  the scope `repositories:write`, one with the role `review` does not, and
  `POST /api/v1/management/enrolments` takes `repositoryWrite` directly. Every Runner
  principal may read. A host enrolled before this feature reads only; enroll it
  again with its role to change that.
- Monolith: enroll the runner with the scope `repository.write` or
  `repository.read` in addition to its other scopes.

A Task Server needs `TaskServer:AttemptCredentialWrappingKeyFile`, because the
connection lives in its encrypted root store.

## Keep the tunnels out of the operator's routes

The local profile has no sign-in, and a host's SSH tunnel makes its API reachable
from that host. Give the browser path a port of its own:

```sh
export OPERATOR_PORT=5032   # in the launcher that starts the Studio
```

`api.sh` then also listens on that port, and the frontend proxy must use it. Point
tunnels at the ordinary port, and do not forward the frontend through a tunnel.

On the ordinary port a write then passes only when it is what a host does anyway:
the Runner service routes, registering a client, redeeming a one-time enrollment
code, and the Task API's card operations with start, stop and continue. Everything
else answers only on the operator port, including this page, this installation's
access, its secrets, project settings and the project URL commands that run on this
machine. The refusal is `operator-listener-required`, logged once per route.

Your own scripts that change those things need the operator port as well. A website
that rebinds its name to this machine is refused every write on every port by its
`Origin`.

## How Git finds the token

Tokens live in a `git credential-store` file: `github-app/git-credentials` under
the host's state directory, or `AgentStudio/github-app/git-credentials` under the
application data folder of the Studio host. Beside it lies a generated Git config
with one `[credential]` section per covered repository, which every platform Git
process includes.

A repository the App does not reach matches no section, so everything the host
configured for it keeps working untouched. A covered repository uses only the
token store, through a helper that only answers `get`: Git hands a used credential
to all helpers afterwards, so a storing helper would copy a one-hour token into a
long-lived store. A process that isolates its Git configuration, such as a proof of
one specific token, is left alone.

Review workspaces use a cleared environment and select their credential
themselves; they read the same tokens from `github-app/review-credentials`.

Coding CLIs do not get the settings, the host key's path or the token files' path.
This is not a boundary against a determined agent: a CLI runs under the same
operating-system user as the host service, so it could find the files, exactly as
it could read `~/.git-credentials` before. Tokens at least expire within the hour.
Running CLIs under their own user is the real fix and is still open.

A scheduled coverage or post-merge run shares the tokens when its unit sets
`RUNNER_STATE_DIR`.

## Moving away from personal tokens

Existing credentials keep working: a repository the App does not reach falls back
to what the host had configured. The settings page lists which repositories are
covered. Once all are, remove the old entries from `~/.git-credentials` and
`RUNNER_REVIEW_GIT_CREDENTIALS_FILE` on the hosts, and revoke the tokens on GitHub.
A remote rewritten to SSH, such as by a deploy key's `url.*.insteadOf`, keeps using
that key until you remove the rewrite.

Check repository rules before relying on the App for protected branches: a ruleset
that names people or teams in its bypass list needs the App added as well.

## Problems

| Symptom | Meaning |
|---|---|
| Repository shows **App not installed** | Add it with **Add repositories or an account**. |
| Repository shows **Needs confirmation** | Choose **Confirm installations**. |
| Host log: `repository-credentials refused status=401` | The key file is missing, or the key was revoked. |
| Host log: `repository-credentials refused status=403` | A read key asked for write access: the host's role and key disagree. |
| No hosts listed | The list is kept in memory; it fills again within five minutes of a restart. |
| The private key reads as revoked | It was revoked in **Settings > Secrets**. Reconnect with a new key. |
| `operator-listener-required` | The request did not come through the browser path. Use the page on the Studio machine. |
