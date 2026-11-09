# Isolated worktree test stack (dynamic ports)

> Bring up a task worktree's **own** backend (and optionally frontend) on
> **dynamic free ports**, run real integration / E2E tests against it, then tear
> it down. Companion to [concepts/parallel-task-execution.md](../../concepts/parallel-task-execution.md)
> and the run-outcome / test-quality-gate contract in
> [contracts/run-outcome.md](../../system/contracts/run-outcome.md).

## Why

The long-lived stacks own fixed ports: stable on backend `:5031` / frontend
`:4011`, dev on `:5030` / `:4010`. A task running inside a `git worktree`
therefore cannot bind those ports to stand up its own backend for tests, and two
parallel worktree runs would fight over them. That blocks a real test gate (a
suite against a living backend) for worktree runs.

The worktree test stack solves this by:

- allocating **free ephemeral ports** (no fixed `:5031`/`:4011` conflict),
- booting the backend against an **isolated temp workspace** so it can never
  become a second pickup driver on the shared workspace,
- handing the ports to tests via well-known env vars (`BACKEND_PORT`,
  `FRONTEND_PORT`, `PW_BASE_URL`, `PW_BACKEND_URL`),
- tearing everything down cleanly afterwards.

Because each worktree allocates its own ports and its own temp workspace,
**parallel worktree runs do not collide.**

## Usage

```sh
# Backend only (most integration / REST-driven specs need just this):
bash scripts/worktree-test-stack.sh up

# Backend + Angular dev server (full UI E2E), proxy auto-points at the backend:
bash scripts/worktree-test-stack.sh up --with-frontend

# Make the ports available to your test runner:
eval "$(bash scripts/worktree-test-stack.sh env)"

# Run tests (Playwright + the REST helper both read these env vars):
npm --prefix frontend run e2e            # uses PW_BASE_URL / PW_BACKEND_URL

# Health / teardown:
bash scripts/worktree-test-stack.sh status
bash scripts/worktree-test-stack.sh down
```

`env` prints `export KEY=VALUE` lines; `eval` them (or `source` the file at
`.worktree-test-stack/stack.env`) into the shell that runs your tests.

## What the env file exports

| Var | Meaning | Consumed by |
|---|---|---|
| `BACKEND_PORT` | dynamic backend port | `frontend/proxy.dynamic.cjs` |
| `FRONTEND_PORT` | dynamic frontend port (only with `--with-frontend`) | your runner |
| `BACKEND_URL` / `PW_BACKEND_URL` | `http://127.0.0.1:$BACKEND_PORT` | `frontend/e2e/helpers/api.ts` |
| `PW_BASE_URL` | frontend URL if served, else the backend URL | `frontend/playwright.config.ts` |
| `TaskRepository` | isolated temp workspace the backend runs against | the backend |

## How the pieces fit

- **`scripts/find-free-port.mjs`** - robust OS-assigned free-port allocator
  (binds to port `0`, reserves N distinct ports at once). `--self-test` asserts
  it works.
- **`frontend/proxy.dynamic.cjs`** - Angular dev-server proxy that reads
  `BACKEND_PORT` from the environment instead of hard-coding `:5030`/`:5031`.
- **`api.sh` worktree mode** - `ATP_WORKTREE_TEST_BACKEND=1` lets a non-stable
  checkout boot a backend on a dynamic port, but **only** when `TaskRepository`
  points at an isolated workspace. This keeps the ADR-0044 dev-backend boot gate
  intact: the dev/worktree checkout still cannot silently start a second pickup
  driver on the shared workspace.
- **`scripts/worktree-test-stack.sh`** - the lifecycle orchestrator that wires
  all of the above together (`up` / `down` / `env` / `status`).

## Safety invariants

- The backend always runs against a **unique temp `TaskRepository`** (empty =>
  no projects => inert pickup loop). The shared workspace is never touched.
- `api.sh` **refuses** `ATP_WORKTREE_TEST_BACKEND=1` without an isolated
  `TaskRepository`, mirroring the in-process xunit isolation guard in
  `backend/Program.cs`.
- If the worktree carries a `backend/appsettings.Local.json` (whose `WatchPaths`
  could pull in shared projects), `up` refuses unless
  `ATP_WORKTREE_TEST_ALLOW_LOCAL_CONFIG=1` is set as an explicit acknowledgement.
- `down` only removes a workspace path that matches `*atp-worktree-test-*`.
- Quota probes are off (AGT-3593). With `ATP_WORKTREE_TEST_BACKEND=1`, the
  `public-demo-readonly` profile, or `Quota:Mode=demo`, `QuotaService` never
  spawns the host's CLIs and never reads a quota cache file. It serves the
  pinned demo snapshot from `DemoQuotaSnapshots` (fixed percentages, current
  timestamps), so captures show the footer quota without exposing the runner
  host's real subscription usage.

## Verifying

```sh
bash scripts/worktree-test-stack.test.sh   # port allocator, proxy, api.sh guards
```

The script exercises the parts that don't require a .NET build. The full
boot/teardown round-trip is exercised by running `up` then `down` directly.

## Not yet wired

This is the **capability** (scripts + dynamic proxy + isolated backend). Auto-
invoking it from the run pipeline so every worktree task run gets a live test
backend by default is a follow-up slice on
[concepts/parallel-task-execution.md](../../concepts/parallel-task-execution.md);
it is intentionally out of scope here.
