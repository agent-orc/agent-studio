# Identity and Project Bootstrap (standalone Task Server)

This page is the I05 contract from the
[deployment story Dossier](../../deployment-story/index.html) (D5, option A:
one installer-owned bootstrap feeding server-owned human, principal and
project records). It applies to the standalone Task Server plane
(`/api/v1`), which is the selected owner for the installed profile. The
legacy networked backend keeps its own runbook in
[networked-task-server.md](./networked-task-server.md); its cookies, `enr.*`
codes and `/api/auth/*` routes are a different contract and do not work
against the standalone Studio BFF.

`X-Client-Id` and `X-Actor-Id` are attribution hints only. They never
authenticate anything and are ignored by every route on this page.

## Identities

| Identity | Created by | Credential | Revoked by |
|---|---|---|---|
| Installation | First store start | `installationId` (`ins_*`, public) | Never; migration preserves it |
| Owner | First login with the installer code | Password and one recovery code (`rcv_*`) | Recovery replaces both |
| Operator / viewer | Owner (`POST /api/v1/studio/auth/users`) | Password, change on first login | Owner |
| Studio edge, engine, each runner host | One-time enrolment (`enr_*`) or Compose bootstrap file | Principal bearer (`ats_*`) in a restricted host file | `POST /api/v1/management/principals/{id}/revoke` |

Every one-time secret is shown once and stored only as a SHA-256 hash.
The browser never receives an `ats_*` bearer: the Studio BFF keeps its own
bearer in-process and relays only the human session cookie.

## First owner

1. The installer creates `owner_bootstrap_code` beside the service
   credentials (`scripts/compose-secret-bootstrap.sh`, mode 600) and the Task
   Server reads it through `OWNER_BOOTSTRAP_CODE_FILE`.
2. While no owner exists the server arms that code. Read it over the
   administration channel only:
   `docker compose exec task-server cat /run/agent-studio-secrets/owner_bootstrap_code`.
3. Sign in through the Studio edge and call
   `POST /api/v1/studio/auth/bootstrap` with `username`, `password` and
   `bootstrapCode`. The response carries the session and the owner's
   `recoveryCode`. Store the recovery code offline; it is the recovery custody
   for this installation.

Closure rules:

- After the owner exists, bootstrap returns `409 studio-already-bootstrapped`
  and the armed code is deleted.
- Re-running the installer never creates a new owner. A different code file is
  ignored while the original code is still armed (interrupted setup); the
  server logs the mismatch and keeps the original. Restore the original file to
  finish first login.
- Only a loopback `AUTH=none` development store accepts bootstrap without a code.

`GET /api/v1/installation` is unauthenticated and reports `installationId`,
`ownerBootstrapped` and `ownerBootstrapArmed`. It shows no secrets.

## Recovery

`POST /api/v1/studio/auth/recover` takes `username`, `recoveryCode` and
`newPassword`. It replaces the password, revokes every session for the user,
consumes the code and returns a new one once. `POST
/api/v1/studio/auth/recovery-code` reissues it explicitly after
password confirmation. Nothing rotates it implicitly.

## Enrol a host principal

Owner session required (`403 owner-role-required` for operators and viewers;
`401` for the edge bearer without a session):

```bash
# From the owner's administration client, through the edge.
POST /api/v1/management/enrolments
{"principalId":"runner:build-02","kind":"runner","runnerId":"build-02","timeToLiveSeconds":900}
```

Copy only `enrolmentCode` into a mode-600 file on the target host over SSH.
Then, on that host:

```bash
scripts/enrol-host-principal.sh https://tasks.example.com <installationId> \
    /etc/agent-runner/enrolment-code /etc/agent-runner/runner-auth-token
```

The script refuses a non-HTTPS non-loopback URL, refuses to join an
installation other than the one named, never prints either secret, writes the
credential mode 600 and deletes the spent code. Set
`RUNNER_AUTH_TOKEN_FILE` to the credential file.

Denials are one generic `401 enrolment-denied`; the audit log records the
precise reason (unknown, consumed, revoked, expired, installation mismatch,
principal exists). A consumed code therefore exposes a stolen replay.
Issuing a new code for the same principal withdraws the earlier unused one.
Enrolling an existing principal is `409 principal-exists`: rotate it
explicitly with `/management/principals/{id}/rotate` instead.

## Loss of a host

Revoke that host's principal. Other hosts keep working. Enrol the replacement
with a new runner id; a runner id is never reused by another principal.
Revoke the lost host's repository deploy keys separately.

## Register a project

Owner session required:

```bash
POST /api/v1/projects/registrations
{"projectId":"prj-alpha","workspaceId":"...","name":"Alpha","taskKeyPrefix":"ALPHA",
 "repositoryUrl":"https://github.com/org/alpha.git","integrationRef":"develop",
 "releaseRef":"main","deliveryPolicy":"reviewed-publication"}
```

- The project id is stable and required.
- The repository URL is canonicalized (`https://`, `ssh://git@` or
  `git@host:path`). Embedded credentials, query, fragment, `http://`,
  `file://` and local paths are rejected.
- One repository belongs to one project (`409 repository-owned-by-other-project`).
- An identical repeat returns `200`; any differing value is
  `409 project-repository-registered`, never an implicit change.

## Prove the repository from the host

```bash
scripts/probe-project-repository.sh https://tasks.example.com build-02 prj-alpha \
    /etc/agent-runner/runner-auth-token
```

The script runs `git ls-remote` for the integration ref and a dry-run push
against the **registered** origin, then posts the receipt to
`POST /api/v1/runners/{runnerId}/project-probes/{projectId}`. The verdict is
`admitted` only if the observed fetch and push URLs equal the registered origin
and both succeed. A probe that passed through a runner fallback remote
(`runner.env.template` fallback Git URLs) is stored as
`fallback-remote-only`, not admitted. Fallback remotes are diagnostic only.
`GET /api/v1/projects/{projectId}/repository` lists the registration and
every host's latest receipt.

## Upstream failure

The Studio BFF holds no task store. If the Task Server is unreachable it
returns `502 task-server-unavailable` and writes nothing locally.

## Known limits

- Claim admission does not yet consult the probe receipt; the receipt is the
  registry record that I09 journey proof and placement must read.
- No Studio screen presents these flows yet. Any UI claim needs pinned
  screenshots (I09) before it enters the installation guide.
