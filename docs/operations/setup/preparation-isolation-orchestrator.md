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

The project definition is read from the exact subject commit for both coding runs and the build-test gate. Central Build Profile values remain a compatibility fallback only when no repository definition exists. The Settings > Execution view shows repository truth and the latest manifest. An exceptional override requires a written justification and is recorded for review; it does not silently replace the subject-commit definition.

Onboarding detects the existing stack and creates a proposal card containing both files. Accepting the card keeps the contract in the project repository. A preparation failure creates a deduplicated proposal card with the failure signature and reasoning. The orchestrator and user therefore co-author the definition through normal reviewed work.

## Product building blocks and caches

The executor supplies these general building blocks:

| Block | Repository input | Executor cache | Success evidence |
|---|---|---|---|
| Node and npm | `.nvmrc`, `package-lock.json` | content-addressed npm cache | observed Node version and cache key |
| .NET and NuGet | `global.json`, `packages.lock.json` when present | content-addressed NuGet package cache | observed SDK version and cache key |
| Playwright | Node lockfile and Playwright configuration | content-addressed browser cache | browser cache key |

A cache miss restores into a private staging folder under the per-run root and publishes it as an immutable entry only after a green prepare, so poisoned dependencies are never written back. A cache hit binds the prepare process to the existing entry directly; there is no per-run copy of the package folder, and whatever the prepare script adds on a hit stays where the following commands read it. `preparation-manifest.json` records the subject SHA, definition hash, observed versions, lockfile hashes, duration, per-block cache state, a stable failure signature, and, for a failed run, `failureOutputTail`: the bounded tail of what the script printed (stderr, or stdout when stderr stayed empty). A shortened single-line excerpt of that tail is appended to `failureReason`, so the card's integration failure detail names the real error instead of only an exit code. A second unchanged run should report `hit` for every configured block.

The standalone Linux Runner keeps one clean checkout for each project and executor. It fetches and fast-forwards the integration branch only, then leases a separate worktree at the card's subject commit. The stable checkout is never a coding workspace. It keeps product caches warm and is the baseline for later integration runs.

## Preparation environment for later commands

Restoring dependencies is only half the contract. The location they were restored into is written into the build inputs: `project.assets.json` records the absolute package folder that `NUGET_PACKAGES` named during restore, Playwright records its browser directory, npm its cache. A command that starts without those variables therefore fails against a location it cannot see, which is what the Windows merge gate reported on 15.09.2026: a green 7.2 s preparation followed by `dotnet build --no-restore -c Release` ending in `NETSDK1064: Package xunit.analyzers, version 1.4.0 was not found. It might have been deleted since NuGet restore.`

The decision is therefore **preparation publishes its resolved locations, and every later command of the same gate or run receives them**; the working folders are not kept alive until the gate ends. Concretely:

- Every cache block of the manifest carries `environmentVariable` (for example `NUGET_PACKAGES`) and `contentPath`, the resolved package location. On a miss that is the freshly published entry content, on a hit the entry the prepare process was already bound to, and on a discarded block it stays null.
- Build, test, lint, e2e and dependency-preparation commands of the build/test gate run with exactly those variables; the gate logs one `# preparation environment: <VAR>=<path>` line per block, so a later package error is diagnosable from the gate evidence alone.
- A coding run binds the same locations to its prepared workspace, and the agent CLI is started with them on the local orchestrator and on the standalone runner. The runner persists them in the detached worker specification, so a resumed attempt and a reattaching daemon relaunch with the identical locations.
- Because the resolution is a projection of `preparation-manifest.json`, any consumer that only has the manifest derives the same environment.
- A cache hit and a cache miss both leave `dotnet build --no-restore` and `npm test` working, and both resolve to the same immutable entry content.

The published entry stays the shared package folder for the commands that follow, which is how NuGet and npm expect a global package folder to be used: later commands may add packages to it, nothing removes or rewrites an entry. Eviction remains an orchestrator action.

Until a Windows Studio host runs a version carrying this contract, this repository's own `.agent-studio/prepare.ps1` keeps its interim `Remove-Item Env:NUGET_PACKAGES`, which forces the restore into the user's global packages folder. It can be removed once the host executing the Windows merge gate is updated; the gate then supplies the location itself.

## Boundaries for later stages

- M2 adds executor profiles. A sandboxed process is the default, a container is optional, and a micro-VM is reserved for Docker proof. The recorded Windows decision remains Docker mandatory for coding execution. The `windows-host` profile is limited to Host Manager, Installer, and Connector tests.
- M3 adds the full diagnosis and healing loop: cache eviction and retry, missing-tool installation, flaky-test quarantine with a stabilization card, `project.yml` change proposals, enriched follow-up prompts, root-cause cards, and explicit waits.
- Migration proceeds on the Linux Runner first, with orchestrator work in parallel and workstations last. The pilots are Agent Studio, Quality Studio, and Token Economy. Changes fix forward; there is no prolonged dual execution model.
- CI/CD remains a product feature. Later stages extend `project.yml` pipelines with integration-branch runs, deploy stages, test history, and per-step duration, CPU, IO, token, and failure-frequency statistics. External CI stays optional.

## Operator checks

1. Open Settings > Execution and confirm the repository definition is valid.
2. Run a card and inspect `preparation-manifest.json` in its results.
3. Run the unchanged subject again and confirm every cache state is `hit`.
4. Confirm the gate output names one `# preparation environment:` line per cache block and that the build step after both a miss and a hit passes.
5. Break the prepare command deliberately in a test project. Confirm no immutable cache entry is published, a proposal card includes the failure signature, and the manifest's `failureOutputTail` plus the card's integration failure detail show the script's own error text.
6. On a Windows Studio, run a card for a repository without `.agent-studio/prepare.ps1` and confirm preparation runs the POSIX script through Git Bash.
7. Confirm the stable checkout is clean, follows the integration branch by fast-forward only, and has no task edits.

The schemas are [`project-execution.schema.json`](../../app/schemas/project-execution.schema.json) and [`preparation-manifest.schema.json`](../../app/schemas/preparation-manifest.schema.json). The source decisions and migration plan remain in the [Docker execution-world migration Dossier](../docker-ausfuehrungswelt-migration/index.html).
