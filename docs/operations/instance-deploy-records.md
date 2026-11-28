# Report Studio deploys to the Studio (AGT-3879)

The operator scripts deploy the Studio outside the Studio: they build the
server image, the orchestrator engine, the runner host package, the gate worker
release and the review host image for one develop commit, switch the server,
then update the hosts (see [deploy-drain-procedure.md](deploy-drain-procedure.md)).
With the calls below the Studio records each deploy, and the Bereitstellung
area of the Studio's own source project shows:

- **Production · this Studio**: the live release (version, develop commit, live
  since) and the cards it brought, that is the cards first landed in the
  first-parent range since the previous live release;
- **Integrated, not live yet**: the cards accepted and on develop after the live
  commit, in landing order, so the next deploy's content is visible;
- the latest run with every reported step. A run that reports a failed step
  shows **failed at <step>**; a run that reports nothing for 30 minutes shows
  **stalled after <step>**.

Workspace / Deployments opens with the running installation, grouped by
reporting machine. The running build identifies the API. The Structure host
reports identify runner roles, containers and gate heartbeats. A matching
version string alone cannot establish commit alignment. A switch receipt gives
the live time. A process observation gives the earliest observed time.

Waiting to ship uses the Releases view's verified ancestry comparison against
the integration tip. It groups cards by their area tags and derives impact
flags from the paths in attributed Git diffs. The tip verification quotes the
post-merge receipt for that exact commit. History uses the operations deploy
journal when available, with running-revision observations and instance deploy
reports as the fallback. An observed revision has no measured deploy duration.

The matrix includes declared targets and registered project URLs. Each URL
uses the existing registry readiness probe. A successful response can identify
its commit with an `X-Build-Commit` header or a `build-commit` or `git-commit`
HTML meta tag in the first 32 KiB of the page. A declaration takes precedence
over a URL with the same name. Expand the project count at the bottom to see
the watched projects that have no target.

Card badges of the source project's cards then read
`Production · live since <UTC time> · <version>`; badges of declared
environments keep `<Environment> · Live · <version> · <date>`. The flow counter
`deployed` counts the cards that went live today.

The source project is the project whose repository holds the running commit.
Set `InstanceDeploy:SourceProject` in the instance configuration to name it
explicitly. On the first start after this change the Studio records the running
version (the build identity behind `GET /api/system/version`) as the live
release; it brings no cards, and older deploys are not backfilled.

## Credential

The record API accepts only a **deploy reporter token** (scope
`deploy.report`). A Studio session, an API token, or a Runner credential gets
`401`. The token can record deploys and nothing else: it reads no data and has
no workspace write.

Create one once with an Owner session (any Owner credential the operator
already uses with `studio-op` works) and store it in a file only the deploy
user can read. The secret is returned once; `jq` writes it straight to the
file, so it never reaches the terminal:

```sh
umask 077
curl -fsS -X POST "$STUDIO_URL/api/instance/deploy/reporter-tokens" \
  -H "Authorization: Bearer $(cat "$OWNER_TOKEN_FILE")" -H 'Content-Type: application/json' \
  -d '{"name":"deploy-scripts"}' | jq -r .secret > "$HOME/.config/studio/deploy-reporter.token"
```

`GET /api/instance/deploy/reporter-tokens` lists tokens (never secrets) with
their last use; `DELETE /api/instance/deploy/reporter-tokens/<id>` revokes one.
Both need the Owner.

## Recipe for the deploy scripts

Source these functions in the deploy script. `curl` reads the bearer header
from standard input (`-H @-`), so the token is neither printed nor visible in
the process list. Reporting is best effort: a failed report never stops the
deploy (`|| true`), and every call can be repeated.

```sh
STUDIO_URL=${STUDIO_URL:?}                       # the instance's own base URL
DEPLOY_REPORTER_TOKEN_FILE=${DEPLOY_REPORTER_TOKEN_FILE:-$HOME/.config/studio/deploy-reporter.token}

studio_report() { # method path json
  printf 'Authorization: Bearer %s\n' "$(cat "$DEPLOY_REPORTER_TOKEN_FILE")" |
    curl -fsS -o /dev/null --max-time 10 -X "$1" "$STUDIO_URL/api/instance/deploy/runs$2" \
      -H @- -H 'Content-Type: application/json' -d "$3" || true
}

# Start: one run id per deploy, the full develop commit, and each component version.
RUN_ID="$(date -u +%Y%m%dT%H%MZ)-${COMMIT:0:9}"
studio_report POST "" "$(jq -nc --arg id "$RUN_ID" --arg c "$COMMIT" --arg v "$VERSION" \
  '{runId:$id, targetCommit:$c, version:$v, components:{server:$v, engine:$v, runner:$v, "gate-worker":$v, "review-host":$v}}')"

# Each step: report running when it starts and the outcome when it ends.
step() { # name state [message]
  studio_report PUT "/$RUN_ID/steps/$1" "$(jq -nc --arg s "$2" --arg m "${3:-}" '{state:$s} + (if $m == "" then {} else {message:$m} end)')"
}
step build:server running;   step build:server succeeded
step server_switch running;  step server_switch succeeded   # the release is live from here
step coding_install:host-a running; step coding_install:host-a failed "install exited 1"

# End: succeeded or failed.
studio_report POST "/$RUN_ID/finish" '{"outcome":"succeeded"}'
```

Step names are lowercase letters, digits, `:`, `.`, `_` and `-`. The kind is
taken from the prefix, so the journal names of `deploy/networked/deploy.py`
can be sent unchanged:

| Prefix | Kind |
|---|---|
| `build` | build of one component |
| `switch`, `server-switch`, `server_switch` | server switch; its success takes the release live |
| `host`, `coding_install`, `coding_health`, `model_probe`, `host_drain`, `host_resume` | coding host |
| `review-host`, `review_copy`, `review_build`, `review_idle`, `review_recreate`, `review_verify` | review host |

A body may also name `kind`, `startedAtUtc`, `finishedAtUtc` and a `message` of
at most 512 characters.

## Repeating a call

- **Start** with the same `runId`, commit and version answers the stored run;
  a different commit or version for that id is `409`.
- **Step**: the same report twice changes nothing. A `running` report never
  reopens a finished step. A finished report replaces the step, so a retried
  step that now succeeded clears its failure; the first start time stays.
- **Finish** with the same outcome answers the stored run; the other outcome is
  `409`.

The server restarts during its own switch. Report `server_switch succeeded`
after the new server answers its health check; the call then reaches the new
server, which keeps the records under the task repository
(`deployments/instance/state.json`).
