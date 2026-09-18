# Runner-host resource governance

Status: Decided for Linux runner hosts on 2026-07-25.

The `agent-host` package owns resource policy for every runner service it
installs or updates. Parallelism inside a task is not a capacity unit. A single
review slot can start MSBuild nodes, Roslyn servers, xUnit workers, browsers, and
containers. The operating system cgroup is therefore the hard execution
boundary.

This decision follows the incident in which eight Review Plane slots produced a
load of 190 on a 12-core host. Tool-specific hints such as
`MSBUILDNODECOUNT` remain useful optimizations, but they are not enforcement and
are not part of the capacity model.

## One capacity truth

The host-level capacity model from AGT-2228/2302 remains the only source of
truth:

- Central admission uses host measurements and adjusts the runnable slot target
  with AIMD. Slots describe admitted work, not CPU entitlement.
- The Linux host enforces the final CPU, I/O, and optional memory envelope for
  each role through systemd/cgroup v2.
- A slot controller may reduce concurrency before saturation. It may never
  expand a role beyond its cgroup envelope.
- Runner processes report occupancy and host measurements. Projects do not own
  independent capacity settings.

The two controls are complementary. AIMD controls how many jobs enter; cgroups
bound the fan-out of the jobs already admitted.

## Linux role defaults

`agent-host` evaluates `nproc` during every install or update and writes the
resolved values into the main unit definition.

| Role | Unit | `CPUQuota` | `CPUWeight` | `IOWeight` | `MemoryMax` |
|---|---|---:|---:|---:|---|
| Coding | `agent-runner.service` | all logical CPUs, `nproc * 100%` | `100` | `100` | omitted |
| Review | `agent-runner-review.service` | logical CPUs x review slots / all role slots, minimum `100%`, never the whole host | `30` | `30` | omitted |

For example, the reference 12-core host with two Coding and two Review slots
receives `CPUQuota=1200%` for coding and `CPUQuota=600%` for review. The lower
Review weights ensure that Coding wins contention while unused host capacity
remains available to other cgroups. The Review quota is also the hard input to
its slot ceiling: at the documented 2-core minimum it can supply at most three
Review workers.

`MemoryMax` is intentionally absent by default. A blind derived memory cap can
turn a large build into an OOM failure without knowing the repository working
set. Operators may set it deliberately after measuring the host.

## Per-worker envelope below the role (AGT-2866)

The role envelope above bounds a plane. It cannot stop one run inside that plane
from consuming all of it, which is what happened on 17.09.2026 when three coding
runs started 24, 49, and 104 CPU busy loops and drove a 12-core host to a load
average of 76, 35, and 41 while an unrelated review suite flaked.

Every detached worker therefore also carries its own cgroup below the role unit.
The unit declares `Delegate=cpu pids` and `DelegateSubgroup=daemon` (systemd 254
or newer), so systemd parks the daemon in a `daemon/` leaf and the daemon creates
one `worker-<id>/` per worker with `cpu.max`, `cpu.weight`, and `pids.max`
derived from the host slot split:

| Value | Derivation | 12 cores, 2 coding + 2 review |
|---|---|---:|
| cores per slot | `cores / (RUNNER_HOST_CODING_SLOTS + RUNNER_HOST_REVIEW_SLOTS)` | 3.00 |
| `cpu.max` | cores per slot x `RUNNER_WORKER_CPU_BURST` (2.0), at least one core, never more than the host | 600% |
| `cpu.weight` | 100 per worker, 1000 for the `daemon/` leaf | 100 |
| `pids.max` | cores per slot x 512, clamped to 1024..4096 (counts threads; a fork bomb fuse, not a throttle) | 1536 |

The leaf is systemd's job on purpose. A daemon that moved itself out of the unit
cgroup would leave that cgroup distributing controllers, and `KillMode=process`
keeps it alive across a restart, so systemd could not attach the replacement main
process to it: the unit then fails with `219/CGROUP` for as long as one worker
survives.

This layer is subordinate, not a replacement: the role `CPUQuota` and `CPUWeight`
above remain the cross-plane arbitration this document decided, and the
per-worker `cpu.weight` is uniform so that no worker of a role can starve a
sibling or the daemon renewing its lease. Transient `systemd-run --scope` units
were rejected for this layer: the service account would need a polkit grant to
create one, and a scope would sit outside the role unit and escape the aggregate
above. Operator detail is in
[the Linux agent host runbook](../../setup/linux-runner-host.md#per-worker-resource-envelope).

## Convention and explicit profile

There are no environment-specific resource overrides. Missing values are
derived from host properties. The only supported override is the operator-owned
Linux profile at `/etc/agent-host/profile.conf`:

```ini
# Omit keys to retain host-derived defaults.
CODING_CPU_QUOTA=800%
CODING_CPU_WEIGHT=100
CODING_IO_WEIGHT=100
CODING_MEMORY_MAX=24G

REVIEW_CPU_QUOTA=400%
REVIEW_CPU_WEIGHT=30
REVIEW_IO_WEIGHT=30
REVIEW_MEMORY_MAX=8G
```

This profile is host bootstrap policy. It is not a project setting, runner env
setting, or centrally mutable task value.

## Unit ownership and update behavior

The install and update path performs the same idempotent reconciliation:

1. Read the role policy from the agent-host profile.
2. Derive every missing value from `nproc` and the role defaults.
3. Inspect `/etc/systemd/system/<unit>.service.d/*.conf` for legacy
   `CPUQuota`, `CPUWeight`, `IOWeight`, and `MemoryMax` directives.
4. Adopt legacy values into missing profile keys so an operator's intentional
   limits survive the migration.
5. Remove the migrated directives from drop-ins. Delete a drop-in only when no
   other effective settings remain.
6. Write the resolved directives into the main unit, run `daemon-reload`, and
   restart the role service.

The result is reviewable with:

```bash
systemctl cat agent-runner.service
systemctl cat agent-runner-review.service
systemctl show agent-runner.service \
  -p CPUQuotaPerSecUSec -p CPUWeight -p IOWeight -p MemoryMax
```

Manual resource drop-ins are no longer an operating procedure. Re-running the
host install/update path converges both new and existing units on the
agent-host-owned definition.

## Platform boundary

This implementation is Linux-only because systemd cgroup controls are the
enforcement mechanism. Windows resource enforcement is explicitly out of scope.
Windows Job Objects require a separate design and implementation card. A future
Windows path must preserve the same role-policy and host-capacity semantics
instead of treating runner slot counts as resource limits.
