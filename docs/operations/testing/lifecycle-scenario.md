# Application lifecycle scenario

`scripts/release/lifecycle-scenario.mjs` plays the first run of the
[application-lifecycle Dossier](../application-lifecycle/index.html) (§8, card
AGT-3178) against a real installation. A simulated user takes the project
"Click Counter" from the static web app template to production inside a
weekly quota. The script drives Studio through its documented API and through
a headless Chromium (Playwright from `frontend/node_modules`). It writes one
report per execution. It sits next to the
[installation scenarios](installation-scenarios.md) (AGT-3162); those prove an
installation, this one proves what a user does with it.

## Run it

```sh
node scripts/release/lifecycle-scenario.mjs \
  --studio-url https://studio.v1.internal \
  --user-file ~/.config/agent-studio/lifecycle-user.json \
  --repository-owner agent-studio-scenarios \
  --stage-host v1 \
  --stage-address http://v1.internal:8081 \
  --production-address http://v1.internal:8082
```

`--help` lists every option. The report goes to `--report-dir`, else to
`JOB_RESULTS_DIR`, else to `artifacts/lifecycle-scenario`. Exit code 0 means the
playthrough passed, 1 that a step failed or was skipped, and 2 a usage error.
`--teardown never` keeps everything for inspection; `--teardown only` removes
what an earlier run left.

The sign-in file holds `{"login": "...", "password": "..."}` and, when the
Owner enrolled an authenticator, `"totpSecret": "<base32>"`. It must have mode
600 and belong to the user who runs the script. There is no password option
and no environment variable; a token-shaped value in the environment or on the
command line is refused, as in `studio-op`. The simulated user must be the
Owner: creating a project with a repository and setting quota limits are Owner
actions on a networked installation, and quota writes refuse service-account
tokens.

## What the installation needs

- Stage and production run on the host chosen in Dossier decision D3: V1, the
  internal Debian VM on KORI. `--stage-host` and `--production-host` name it in
  the host registry; the host runs `agent-host deploy-loop` with a deploy
  credential and serves the two release directories at the two addresses.
- Repository provisioning for templates (AGT-3175): the Studio host has its
  GitHub provisioning credential for the test organisation (Dossier §7, own
  test identities).
- Automatic stage (AGT-3176): the release gate submits gate releases with
  `--project`, so a green gate queues stage without a person.
- Quotas (AGT-3177) and a signed-in orchestrator CLI for the project chat.

The script does not configure any of these. A missing one shows as a failed or
timed-out step with the condition it waited for.

## Steps

| Step | What the script does | Passes when |
|---|---|---|
| Setup: sign in | `POST /api/auth/login` with the sign-in file | The user is the Owner |
| Setup: workspace | Creates or reuses "Department demo", removes a leftover "Click Counter", sets the weekly limits (tokens, core-hours, two hosting slots) | The quota shows the limits |
| 1 | Signs in through the Studio sign-in form | The sign-in form is gone |
| 2 | `POST /api/projects` with `createRepository` and template `static-web`; the repository name carries the run id | Studio reports project id and repository |
| Setup: environments | A Ready card adds `docs/deployments/environments/stage.json` and `production.json` and a `health.txt` that the build copies to `dist/`; the user accepts it | Both declarations are readable |
| 3 | Sends the wish to `POST /api/runner/{project}/orchestrator-chat`; confirms once after three minutes if no card appeared | New cards exist |
| 4 | Queues backlog cards, accepts each card in Human Review only with green evidence (the `studio-op accept` policy) | All cards are completed; the quota and the quota panel show use |
| 5 | Waits for the release that carries all wish cards to run healthy on stage; opens the stage address and uses the counter and its reset | Stage answers 200 and has a reset button |
| 6 | Approves production for that digest | Production runs the same digest, healthy |
| 7a | A seeded card removes `health.txt`; it passes the gates and stage; the user approves it | Production health fails, production rolls back to the previous digest, Studio opens a failure card |
| 7b | A fix card restores `health.txt` with a test; stage, approval, production | Production runs the fix and `/health.txt` answers 200 |
| 8 | Lowers the token limit to the tokens already used | Status is warning or blocked, admission refuses new work, the Studio header shows the warning |
| Teardown | Stops stage and production, removes project and workspace | Nothing named for the scenario remains |

Stage checks the page; production also checks `/health.txt`. Stage and
production run the same digest, so this stricter production health contract
is how a seeded change passes stage and fails production. The fix is a
scenario card that names Studio's failure card, because Studio has no route to
amend a card prompt. The failure card stays as Studio's own record.

## Report

`lifecycle.json` and `lifecycle.html` hold, per step: phase, start and end
(UTC), duration, model tokens and core-hours used during the step (the delta of
the workspace quota meter), screenshots, pass, fail or skip, the reason, and
the evidence (card keys, release version, commit, digests, HTTP status, quota
status, admission answer). The head of the report shows the run id, the Studio
origin, the platform label, the stage and production addresses, the totals,
and two verdicts: `status` for setup and scenario, `teardownStatus` for
cleanup. Screenshots are in `screenshots/`, taken at 1440 x 900. A long secret
from the sign-in file is replaced before the files are written.

## Repeating a run

Each run uses its own repository name, so a rerun is never blocked by the
previous repository. Setup reuses the workspace and removes a leftover project
of the same name. It refuses a workspace that holds other projects and a
"Click Counter" in another workspace.

Known gap: `DELETE /api/projects/{id}` keeps the repository, and Studio deletes
a provisioned repository only to undo a failed create. The teardown step
"Remove the repository" is therefore recorded as skipped with the repository
URL, and repositories accumulate in the test organisation until a card adds
the deletion.

## Tests

`node --test scripts/release/lifecycle-scenario/test/*.test.mjs` plays the
whole journey against an in-process fake Studio with a fake clock and a fake
browser. It covers the happy path, reruns, teardown only, a red review, a
failed stage, a chat that needs a confirmation, an authenticator, a non-Owner,
the configuration guards, and the report. `npm --prefix frontend run lint`
runs it as `lint:lifecycle-scenario`.
