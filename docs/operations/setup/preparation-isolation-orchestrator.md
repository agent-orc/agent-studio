# Preparation, isolation, and orchestrator

Agent Studio separates project preparation, run isolation, and recovery policy. This keeps onboarding simple while making tool and dependency versions explicit.

## M1: repository preparation

Every project may carry two files:

- `.agent-studio/project.yml` declares the stack, repository version manifests, prepare/build/test/lint commands, named test suites and expected durations, generated cache paths, capabilities, non-secret environment values, development-server lifetime, and an optional image.
- `.agent-studio/prepare` composes product building blocks. It does not implement another cache manager.

## Prepare entry point and host environment

The prepare script is a POSIX script and stays the single source of truth. How the gate starts it depends on the host:

| Host | Entry point |
|---|---|
| Linux and macOS | `/bin/sh <prepare>` |
| Windows, repository ships `<prepare>.ps1` | `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <prepare>.ps1` |
| Windows, no `.ps1`, Git Bash installed | `bash.exe -e <prepare>` |
| Windows, neither | preparation fails with the named reason `prepare:windows-entry-missing` |

A `.ps1` entry point is optional. `powershell -File` refuses an extensionless script, so a repository without one runs its POSIX script through Git for Windows. Only a `bash.exe` inside a Git installation is used, never the WSL launcher in `System32`. A Windows host with neither entry point reports the named reason instead of an opaque exit code, so the operator sees what is missing.

The prepare process starts from a cleared environment and receives only a fixed, non-secret allow list: `PATH`, `HOME`, `USERPROFILE`, `TMPDIR`, `TEMP`, `TMP`, `LANG`, `LC_ALL`, `SSL_CERT_FILE`, `SSL_CERT_DIR`. On Windows the base variables `SystemRoot`, `windir`, `ComSpec`, `PATHEXT`, `ProgramData`, `ProgramFiles`, `ProgramFiles(x86)`, `APPDATA`, `LOCALAPPDATA`, `HOMEDRIVE`, `HOMEPATH`, `USERNAME`, and `COMPUTERNAME` are copied on top, because MSBuild, NuGet, and the .NET SDK resolve user and machine paths through them. Without them a restore fails with `Value cannot be null. (Parameter path1)` from NuGet.targets. The repository's own `environment:` values and the product cache variables are layered on afterwards and win.

`ProjectPreparationExecutor.Classify` (`contracts/TaskServer.Contracts/ProjectPreparation.cs`) recognizes known signatures in the failed run's stderr/stdout and gives each a named reason instead of the generic `command:<exitCode>:<digest>` fallback:

| Evidence contains | Named signature | Failure kind |
|---|---|---|
| `not recognized as an internal or external command` (or the POSIX `command not found` / `No such file or directory`) | `tool:missing` | `ToolMissing` |
| `Loading managed Windows PowerShell failed` | `prepare:powershell-environment` | `Environment` |
| `Value cannot be null. (Parameter 'path1')` (NuGet.targets) | `prepare:windows-environment` | `Environment` |
| Neither `.ps1` nor Git Bash is available on Windows | `prepare:windows-entry-missing` | `ScriptMissing` |

The two Windows-specific signatures share the same root cause as the `path1` example above: a missing Windows base variable, just surfacing in a different tool (PowerShell's own startup versus NuGet's restore). `Environment` (like every kind other than `Command`/`Definition`) is a host misconfiguration, never a product defect, so the build/test gate classifies it as `BuildTestGateFailureKind.Environment` and the accepted-integration policy keeps the card `Pending` instead of `ConflictSkipped` (CAC-18).

The project definition is read from the exact subject commit for both coding runs and the build-test gate. Central Build Profile values remain a compatibility fallback only when no repository definition exists. The Settings > Execution view shows repository truth and the latest manifest. An exceptional override requires a written justification and is recorded for review; it does not silently replace the subject-commit definition.

Onboarding detects the existing stack and creates a proposal card containing both files. Accepting the card keeps the contract in the project repository. A preparation failure creates a deduplicated proposal card with the failure signature and reasoning. The orchestrator and user therefore co-author the definition through normal reviewed work.

## Product building blocks and caches

The executor supplies these general building blocks:

| Block | Repository input | Executor cache | Success evidence |
|---|---|---|---|
| Node and npm | `.nvmrc`, `package-lock.json` | content-addressed npm cache | observed Node version and cache key |
| .NET and NuGet | `global.json`, `packages.lock.json` when present | content-addressed NuGet package cache | observed SDK version and cache key |
| Playwright | Node lockfile and Playwright configuration | content-addressed browser cache | browser cache key |

An entry is immutable after publication. One validity rule governs publication and lookup: an entry has a readable manifest for its block and key, a content directory with at least one file, and content bytes that agree with `sizeBytes`. A successful block that received no files is not published; the next run sees an ordinary miss. An internally consistent empty entry written by an older executor is also a miss and is replaced when the block next produces content.

A missing manifest, missing content directory, malformed identity, or size mismatch is incomplete. Lookup never fails the run for that condition. It takes the per-entry lock, atomically renames the entry below the cache root's `.quarantine/` directory, logs `state=evicted-incomplete`, and continues from an empty per-run block. A concurrent lookup either copies the valid entry before the rename or observes the miss after it; both runs proceed. The normal retention sweep removes quarantine directories. A prepare command that still reports a cache-class failure quarantines the entries it restored from, and an integration gate makes one immediate clean retry. The failure remains an environment classification, not a review verdict, and the retry is recorded in gate evidence and the card timeline.

A failed prepare run discards its whole run root, so poisoned dependencies are never written back. `preparation-manifest.json` records the subject SHA, definition hash, observed versions, lockfile hashes, duration, per-block cache state, content bytes, recovery, a stable failure signature, and, for a failed run, `failureOutputTail`: the bounded tail of what the script printed (stderr, or stdout when stderr stayed empty). A shortened single-line excerpt of that tail is appended to `failureReason`, so the card's integration failure detail names the real error instead of only an exit code. A second unchanged run should report `hit` for every non-empty configured block.

An empty binding records `state: unused` and its consecutive successful-run count. After three consecutive unused runs, Settings > Execution shows a definition warning naming the block and bound environment variable. The check also recognizes portable and PowerShell forms that unset the variable, so a repository owner sees that the prepare script redirects the package folder instead of silently carrying a useless cache declaration.

## Cache binding for the commands that follow preparation

Preparation is not finished when the prepare script exits: whatever it restored has to stay resolvable for the build, test, lint and e2e commands of the same gate or coding run. The contract is one binding, owned by the run:

- Every block works in a per-run folder under `<product cache root>/.runs/<run id>/<block>`. On a cache miss the folder starts empty; on a hit it starts as this run's own copy of the published entry. A run never executes inside a published entry, so a failing or concurrent prepare cannot corrupt shared state, and deleting the run root rolls the whole attempt back.
- A green miss publishes a *copy* of the per-run folder as the new immutable entry. The per-run folder stays where it is, which is what keeps the run's own `project.assets.json` valid.
- `ProjectPreparationResult.Environment` exposes the resolved locations, keyed by the variable that points at them: `NUGET_PACKAGES`, `NPM_CONFIG_CACHE`, `PLAYWRIGHT_BROWSERS_PATH`. It is empty when preparation is not configured or failed.
- The build/test gate applies that binding to every dependency-preparation and verify process, as the last layer, so it also wins over the gate's own legacy npm cache. A coding run applies it to the agent's CLI launch (backend legacy spawn and CAR overlay, and the standalone runner's worker specification, which a bounded same-session resume reads back from the first attempt's `spec.json`).
- `ProjectPreparationExecutor.ReleaseRunRoot` drops the per-run folder. The gate calls it after its last command; a coding run calls it when the run's slot is freed. A folder left behind by a killed process is reclaimed by age after 24 hours; published entries are never touched by either path.

Without this binding a green preparation is followed by `NETSDK1064: Package <id>, version <x> was not found. It might have been deleted since NuGet restore.` as soon as a verify command uses `--no-restore` (TE-52, Windows merge gate, 15.09.2026). The same applies to `npm test` against a cache the prepare filled and to Playwright browsers for e2e.

Windows interim note: a repository whose `.agent-studio/prepare.ps1` drops `NUGET_PACKAGES` and restores into the user's global packages folder now gets the unused-block warning after three preparations. Remove that repository-side workaround when its Windows Studio runs a Task Server with the binding, then confirm one green cache-miss and one green cache-hit gate run there.

The standalone Linux Runner keeps one clean checkout for each project and executor. It fetches and fast-forwards the integration branch only, then leases a separate worktree at the card's subject commit. The stable checkout is never a coding workspace. It keeps product caches warm and is the baseline for later integration runs.

## Boundaries for later stages

- M2 adds executor profiles. A sandboxed process is the default, a container is optional, and a micro-VM is reserved for Docker proof. The recorded Windows decision remains Docker mandatory for coding execution. The `windows-host` profile is limited to Host Manager, Installer, and Connector tests.
- M3 adds the full diagnosis and healing loop: cache eviction and retry, missing-tool installation, flaky-test quarantine with a stabilization card, `project.yml` change proposals, enriched follow-up prompts, root-cause cards, and explicit waits.
- Migration proceeds on the Linux Runner first, with orchestrator work in parallel and workstations last. The pilots are Agent Studio, Quality Studio, and Token Economy. Changes fix forward; there is no prolonged dual execution model.
- CI/CD remains a product feature. Later stages extend `project.yml` pipelines with integration-branch runs, deploy stages, test history, and per-step duration, CPU, IO, token, and failure-frequency statistics. External CI stays optional.

## Operator checks

1. Open Settings > Execution and confirm the repository definition is valid.
2. Run a card and inspect `preparation-manifest.json` in its results.
3. Run the unchanged subject again and confirm every cache state is `hit`.
4. For both runs, confirm the verify commands passed and that `<product cache root>/.runs` is empty afterwards: the per-run folders are released with the gate, the published entries are not.
5. Break the prepare command deliberately in a test project. Confirm no immutable cache entry is published, a proposal card includes the failure signature, and the manifest's `failureOutputTail` plus the card's integration failure detail show the script's own error text.
6. On a Windows Studio, run a card for a repository without `.agent-studio/prepare.ps1` and confirm preparation runs the POSIX script through Git Bash.
7. Confirm the stable checkout is clean, follows the integration branch by fast-forward only, and has no task edits.

The schemas are [`project-execution.schema.json`](../../app/schemas/project-execution.schema.json) and [`preparation-manifest.schema.json`](../../app/schemas/preparation-manifest.schema.json). The source decisions and migration plan remain in the [Docker execution-world migration Dossier](../docker-ausfuehrungswelt-migration/index.html).
