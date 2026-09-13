# Preparation, isolation, and orchestrator

Agent Studio separates project preparation, run isolation, and recovery policy. This keeps onboarding simple while making tool and dependency versions explicit.

## M1: repository preparation

Every project may carry two files:

- `.agent-studio/project.yml` declares the stack, repository version manifests, prepare/build/test/lint commands, named test suites and expected durations, generated cache paths, capabilities, non-secret environment values, development-server lifetime, and an optional image.
- `.agent-studio/prepare` composes product building blocks. It does not implement another cache manager.

The project definition is read from the exact subject commit for both coding runs and the build-test gate. Central Build Profile values remain a compatibility fallback only when no repository definition exists. The Settings > Execution view shows repository truth and the latest manifest. An exceptional override requires a written justification and is recorded for review; it does not silently replace the subject-commit definition.

Onboarding detects the existing stack and creates a proposal card containing both files. Accepting the card keeps the contract in the project repository. A preparation failure creates a deduplicated proposal card with the failure signature and reasoning. The orchestrator and user therefore co-author the definition through normal reviewed work.

## Product building blocks and caches

The executor supplies these general building blocks:

| Block | Repository input | Executor cache | Success evidence |
|---|---|---|---|
| Node and npm | `.nvmrc`, `package-lock.json` | content-addressed npm cache | observed Node version and cache key |
| .NET and NuGet | `global.json`, `packages.lock.json` when present | content-addressed NuGet package cache | observed SDK version and cache key |
| Playwright | Node lockfile and Playwright configuration | content-addressed browser cache | browser cache key |

An entry is immutable after publication. A failed prepare run discards its staging cache, so poisoned dependencies are never written back. `preparation-manifest.json` records the subject SHA, definition hash, observed versions, lockfile hashes, duration, per-block cache state, and a stable failure signature. A second unchanged run should report `hit` for every configured block.

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
4. Break the prepare command deliberately in a test project. Confirm no immutable cache entry is published and a proposal card includes the failure signature.
5. Confirm the stable checkout is clean, follows the integration branch by fast-forward only, and has no task edits.

The schemas are [`project-execution.schema.json`](../../app/schemas/project-execution.schema.json) and [`preparation-manifest.schema.json`](../../app/schemas/preparation-manifest.schema.json). The source decisions and migration plan remain in the [Docker execution-world migration Dossier](../docker-ausfuehrungswelt-migration/index.html).
