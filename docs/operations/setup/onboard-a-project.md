# Onboard a project

Project onboarding is a product workflow. It registers identity, creates the central task store, activates discovery, and optionally assigns execution. It does not put Agent Studio jobs in the product repository and it does not require a backend restart.

Project onboarding and [remote runner host onboarding](./linux-runner-host.md) are the two independent onboarding axes:

- Onboard a project to define what work exists, where its repository is, and where its tasks are stored.
- Onboard a host to define where work can execute. Then select that host as the project's execution runner.

## Before you start

Choose:

- A display name, such as `Quality Studio`.
- A unique 2 to 6 character short code, such as `QS`. Task keys use this prefix.
- The workspace that should contain the project.
- An optional local Git checkout path, CLI working directory, repository URL,
  or any useful combination of them.
- A default coding CLI/model and project colour. Both can be changed later.
- An optional execution runner. Onboard the host first if it is not listed yet.

For a local runner, the repository path must be an existing absolute Git
checkout on the backend host. The working directory must also exist and may be
the checkout itself or a subdirectory. For a remote runner whose checkout is
not visible on the backend host, omit both local paths and provide the
repository URL. The selected remote host resolves its own checkout.

## UI workflow

1. Open Agent Studio and find the target workspace in the Explorer.
2. Open its actions and choose **New project**.
3. Under **Identity**, enter the workspace, display name, short code, and
   colour.
4. Under **Code location**, enter the local checkout, CLI working directory,
   and/or HTTP(S) repository URL.
5. Under **Default coding agent**, select the default CLI/model, then choose
   the local host or an onboarded runner under **Execution location**.
6. Choose **Create project**.

There is no project-source selection or Project Sources administration step.
Local project onboarding is the available product workflow. A repository URL
does not by itself create a managed checkout.

The dialog closes after the server confirms creation. The project is
immediately present on the board. No settings file edit or backend restart is
needed.

The first-run checklist can register the new project's credential-free clone
URL and integration branch with the Task Server. The repository proof still
comes from a runner that actually fetches and pushes the registered origin;
registration alone does not prove GitHub access. Never include a username,
token, query, or fragment in the clone URL. Studio's local project registry
rejects these URL forms before saving them.
On a local install, the checklist's GitHub verification clones a registered
repository when the project has no checkout and stores the managed checkout
path and CLI working directory on the project. A generated deploy key is
configured in that checkout's Git settings, so later task fetches and pushes
use the same key after restart.

## API workflow

Send `POST /api/projects`. `X-Client-Id` must identify a registered operator or automation client and is included in mutation telemetry. The bootstrap identity is `local-default`.

```sh
curl -i http://localhost:5030/api/projects \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Id: local-default' \
  --data '{
    "displayName": "Quality Studio",
    "shortCode": "QS",
    "workspaceId": "ws-default",
    "repositoryPath": "C:\\Projects\\quality-studio",
    "rootPath": "C:\\Projects\\quality-studio",
    "repositoryUrl": "https://github.com/example/quality-studio",
    "cliDefault": "codex",
    "color": "#569cd6",
    "executionRunner": "agent-runner-01"
  }'
```

`repositoryPath`, `rootPath`, `repositoryUrl`, `cliDefault`, `modelDefault`,
`color`, and `executionRunner` are optional. `repositoryUrl` must be an
absolute HTTP(S) URL. For a board-only project, omit repository and runner
fields. The local folder workflow is implicit; clients do not need to send a
source type.

A successful request returns `201 Created` and the new `PROJ-NNN` record. A
display-name or short-code collision returns `409 Conflict`; malformed values
return `400 Bad Request`; an unknown workspace returns `404 Not Found`.
Retrying a request after a `201` cannot create a second project with the same
identity: the retry receives `409`.

## Validation contract

Both onboarding and later edits use the same rules:

- Workspace, display name, and short code are required.
- Short codes are uppercased, contain 2 to 6 characters, start with `A-Z`, and
  use only `A-Z` or `0-9` after that.
- Display names and short codes are unique across the registry.
- A repository checkout is an absolute local path to an existing Git checkout.
  UNC paths, filesystem roots, missing folders, and folders without `.git` are
  rejected.
- A working directory is an absolute existing local directory. It may be a
  subdirectory and does not need its own `.git` entry.
- A repository URL is either empty or an absolute HTTP(S) URL.
- Optional fields may be left empty during onboarding and set later. An error
  keeps the form open and preserves the entered values so the operator can
  correct only the invalid field.

## What happens automatically

The server performs these steps in one onboarding workflow:

1. Allocates a monotonic registry id such as `PROJ-023` and initializes `NextTaskKeySeq` to `1`.
2. Persists the project in `<TaskRepository>/.metadata/projects.json` and assigns it to the requested workspace.
3. Creates `<TaskRepository>/projects/PROJ-023/tasks/` as the only task-store root for the new project. Lane and task directories are created on demand.
4. Stores the local checkout separately as `RepositoryPath`. It never uses `<repository>/.orchestrator/jobs` for a newly onboarded project.
5. Stores the CLI working directory separately as `RootPath`, falling back to
   the repository checkout when no distinct working directory was supplied.
6. Stores the repository URL as the well-known project URL with id `repo`.
7. Persists the project colour and default coding CLI/model.
8. Adds the project to the scanner and filesystem watcher from the registry,
   without adding a `WatchPaths` setting.
9. Creates a live local runner immediately when the local working directory
   exists, or persists the selected remote-runner assignment.

The board refresh after creation reads the same registry-backed source, so the new empty project is visible immediately.

## Edit project basics later

Creation is not the only chance to enter these values. Open the project, choose
**Settings**, and edit the **Project basics** section. It exposes the same
identity, repository, and coding-default groups as onboarding. The existing
**Execution assignment** card on the same Settings page remains the place to
change the execution runner:

| Group | Editable values |
|---|---|
| Identity | Workspace, display name, short code, colour |
| Code location | Repository checkout, CLI working directory, repository URL |
| Default coding agent | Default coding CLI/model |
| Execution assignment | Local execution or a registered remote runner, saved by the dedicated execution card |

The UI saves the Project basics section through one canonical registry request:

```sh
curl -i -X PUT http://localhost:5030/api/projects/PROJ-023 \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Id: local-default' \
  --data '{
    "displayName": "Quality Studio",
    "shortCode": "QS",
    "workspaceId": "ws-default",
    "repositoryPath": "C:\\Projects\\quality-studio",
    "rootPath": "C:\\Projects\\quality-studio\\src",
    "repositoryUrl": "https://github.com/example/quality-studio",
    "cliDefault": "codex",
    "modelDefault": "<model id from the Codex catalogue>",
    "color": "#569cd6"
  }'
```

`PUT /api/projects/{PROJ-NNN}` has optional patch semantics. Omitted values stay
unchanged. To remove an optional value, send its explicit flag, for example
`{"clearRepositoryUrl": true}` or `{"clearModelDefault": true}`. API clients
may also set `executionRunner` or `clearExecutionRunner` on this endpoint; that
value is delegated to the existing project-settings owner rather than stored in
the registry. The product UI deliberately keeps the runner in its dedicated
execution card and `PUT /api/projects/{projectName}/execution-runner` contract.
Clearing it restores local execution.

The stable project id, central storage location, creation time, and task-key
counter are not editable. Changing the short code does not rewrite existing
task keys; newly created tasks use the new prefix and continue from the
preserved counter. A successful update returns `200 OK`; the same `400`, `404`,
and `409` validation outcomes used during creation apply. Repository/wiki reads
see updated registry paths on subsequent requests. An already instantiated
local runner does not hot-swap its captured project name, repository checkout,
or working directory. Restart the backend after changing any of those three
values and before enabling local auto-pickup. A runner assignment change is
persisted immediately.

## Validate and edit the build profile

`POST /api/projects/{project}/build-profile/validate` runs install and build
commands in the registered `RepositoryPath`, falling back to `RootPath` only
when no repository checkout is available. It never runs in the task-store
`Path`. If neither source workspace exists on the Task Server, the endpoint
returns a conflict instead of recording a misleading product validation
failure. A green remote Review also validates the profile when its immutable
Review plan carries the exact fingerprint of the current profile.

The first profile declaration remains blocked until one of those validations
is green. Editing a profile that was already validated does not silently close
pickup. It preserves the last validated status with `revalidationPending=true`
and admits at most three new coding runs. Each successful claim consumes one
grace run. A matching green local validation or remote Review clears the flag;
if all three runs are consumed first, the build-profile gate blocks further
pickup and surfaces that reason on Ready cards and the workspace banner.

## Configure staged test execution

The build profile declares the complete test inventory. The separate staged
test policy decides how much of that inventory runs for a task lane. Configure
it with `PUT /api/projects/{project}/test-execution`; delete the resource to
restore the default `work-package` level.

```sh
curl -i -X PUT http://localhost:5030/api/projects/Quality%20Studio/test-execution \
  -H 'Content-Type: application/json' \
  --data '{
    "laneLevels": {
      "2-ready": "continuous",
      "4-auto-review": "work-package"
    },
    "continuousCommands": [
      "dotnet test backend.Tests --filter FullyQualifiedName~Smoke"
    ],
    "impactRules": [
      {
        "pathPrefixes": ["frontend/src/app/features/settings"],
        "testCommands": ["npm test -- --include settings"],
        "reason": "settings component ownership"
      }
    ],
    "testHubHistoryPath": ".test-hub/history.jsonl",
    "llmSelectionEnabled": true,
    "llmCliType": "codex",
    "llmModel": "<model id from the Codex catalogue>",
    "llmThinkingLevel": "high"
  }'
```

The stable levels are:

- `continuous`: only the fixed fast baseline;
- `work-package`: the baseline plus impacted test projects, packages,
  configured component rules, and safe Test Hub matches;
- `full`: the baseline plus every test command from the build profile or
  repository discovery.

The release boundary always forces `full` and hard-fail mode. It does not honor
a narrower lane setting. If changed-file evidence is unavailable for an
ordinary work-package run, the planner also falls back to `full` rather than
claiming partial coverage.

Test Hub history is JSONL. A row has `testId`, `command`, optional
`workingSubdir`, `relatedPaths`, `failedAtUtc`, and optional `failure`. History
can select only commands already in the deterministic test inventory, so the
history file is not an executable-command injection surface. The optional LLM
has the same restriction: it may add candidate ids, but it cannot remove the
diff/history selection or supply shell text.

Every card's build/test pipeline reason states the effective level and whether
the full suite ran; the passed status icon exposes this reason on hover.
Detailed reproducibility evidence is stored under the task
as `post-steps/build-test-gate-*.log`: diff input, history input, candidates,
chosen ids and commands, model, and rationale. A failing baseline command that
is outside the selected work package produces
`post-steps/test-findings-*.json` and a visible `warn`; it does not block the
card. A selected work-package failure and every pre-main full-suite failure
remain blocking.

## Store convention

New project task data always lives below the task-server workspace:

```text
<TaskRepository>/
  .metadata/projects.json
  projects/
    PROJ-023/
      tasks/
        lane and task data created by the task API
```

Never create or copy a task store into the product checkout. In-repository stores can expose prompts, logs, attachments, or operational metadata when the repository is pushed, and they mix product source with orchestration state.

TE, CAC, and CAR are documented legacy exceptions. Do not use them as examples
for a new project and do not move them as part of ordinary onboarding. A
purpose-built migration must pause pickup, back up and copy the existing task
data, update `StorageLocation`, verify task counts and keys, and only then
remove the old in-repository copy. This migration is intentionally outside
AGT-2144. Do not hand-edit registry metadata or move folders under the central
task store to perform it.

## Verify the onboarding

1. Confirm `GET /api/projects` contains the new id and `storageLocation` is `<TaskRepository>/projects/PROJ-NNN/tasks`.
2. Create a small task through the UI or task API and place it in Ready.
3. For an executable project, start or enable its runner and confirm the task reaches review.
4. Return to **Project basics**, change one optional value, save, reload the
   project, and confirm the value remains editable and persisted.
5. Clear that optional value and confirm the project still appears and its
   `storageLocation` did not change.
6. Delete the test task.
7. Remove a disposable test project with `DELETE /api/projects/PROJ-NNN`. The endpoint removes its central project store and registry record.

## Troubleshooting

| Symptom | Cause and action |
|---|---|
| `409 shortCode ... is already used` | Choose another 2 to 6 character code. Codes are case-insensitively unique. |
| `400 repositoryPath ...` | Use an existing absolute local Git checkout. For a repository available only to a remote runner, omit the local path and provide `repositoryUrl`. |
| `404 Unknown workspaceId` | Read `GET /api/workspaces` and use a current workspace id. |
| Project exists but auto-pickup is unavailable | A board-only project has no local working directory, or the selected remote host is not ready. Set a valid working directory in Project Settings and restart the backend to instantiate a local runner, or complete [remote host onboarding](./linux-runner-host.md). |
| Project is missing from the board after `201` | Refresh once and inspect `GET /api/workspaces`. If the API listing is also missing, inspect structured `project-onboarded` and `watch-path-activated` log events. No restart should be required. |
| Old project uses `.orchestrator/jobs` | Treat it as legacy. Follow the migration outline above; do not onboard a duplicate project or push that folder. |

For the first real run, continue with [Your first task](./your-first-task.md). For general setup failures, see [Troubleshooting](./troubleshooting.md).
