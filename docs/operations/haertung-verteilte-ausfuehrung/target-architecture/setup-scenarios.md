# OSS setup path decision

**Decision date:** 2026-07-28
**Status:** decided and implemented
**Supersedes:** the three parallel target routes explored by AGT-2304

## Decision

Docker Compose is the only new-user installation path documented for Agent
Studio. The exact start command is:

```sh
docker compose up --wait
```

Since AGT-2729, the default services pull pinned per-service release images
(see [Container images](../../setup/task-server.md#container-images)) instead
of building locally; `--build` only applies to the `dev` profile a source
contributor opts into. The user still clones only `agent-orc/agent-studio` (or
downloads just `docker-compose.yml`). The user does not install .NET or
Node.js, create local application settings, set maintainer-only switches, or
place another repository at a relative path.

The detailed walkthrough is
[Getting started](../../setup/getting-started.md).

## Why Compose won

| Candidate | Decision | Reason |
|---|---|---|
| Docker Compose | Selected | Matches the signed container-default execution direction, already has component Dockerfiles, behaves consistently across host operating systems, and gives startup a health-checkable contract. |
| `dotnet tool install` plus launcher | Rejected for the primary path | Reintroduces a host .NET dependency and would still need to distribute and supervise the Studio frontend and multiple runtime units. |
| Release archive plus launcher | Rejected for the primary path | The signed three-distributable boundary remains valuable for networked production deployment, but combining those assets into a local launcher would create another lifecycle implementation beside Compose. |

The release archives remain the production control-plane distribution described
in [releases.md](../../releases.md). They are not advertised as an alternative
new-user setup.

## First-success boundary

The first successful install is a healthy empty Studio board. It proves:

1. the production frontend loads through its container edge;
2. `/healthz` reaches the API through that same edge;
3. a normal API request succeeds;
4. task data is backed by named volumes; and
5. no Agent Host secrets or coding-agent credentials were required.

Agent Host onboarding is a later, credential-bearing operation. Keeping it out
of first boot prevents repository and CLI authentication choices from becoming
hidden prerequisites for seeing the product.

## Evidence contract

`scripts/compose-smoke-test.sh` is the executable acceptance check. It creates
an isolated Compose project, uses the documented host ports by default, builds
and starts only the default services, waits on the product health contract,
checks the browser shell and a real API response, records service facts, and
removes its test volumes on exit. Parallel CI jobs can opt into dynamically
assigned ports without changing the product path under test. CI runs this same
script.

`scripts/compose-smoke-vm-test.sh` is the clean-machine harness. It verifies a
pinned Ubuntu image checksum, requires KVM instead of falling back to software
emulation, creates a sparse guest disk and cloud-init media, archives the
current worktree without local credentials or build outputs, and preserves the
guest serial log. The original verification reached green only after five
failed boots. Keeping the VM launcher and its cloud-init fixtures in the
repository makes those failures reproducible evidence rather than a one-off
operator claim.

The 2026-07-28 clean-host run used an Ubuntu 24.04 VM with the distribution's
Docker Engine and Docker Compose v2 packages. The VM received only a source
archive of this revision, not any neighbouring repository or host build tool.
The command completed with both services healthy; the edge returned `"ok"`,
the browser shell contained `<app-root>`, and `/api/tasks/grouped` returned
JSON. The durable commands and machine-readable output formats are kept in both
smoke harnesses so the claim can be repeated for every release candidate.
