# Hosted Wiki Publication

How the hosted Wiki advances to the accepted documentation revision without an
operator copying files over SSH, and what to do when a deployment does not
advance.

This page owns the operational contract. The architecture it operates, and why
the hosted Wiki fetches and materializes a revision instead of checking out the
deployment tree, is
[ADR-0074](../../system/architecture/decisions/adr-archive.md#adr-0074---the-hosted-wiki-publishes-an-accepted-revision-by-fetch-and-materialize-never-by-checking-out-the-deployment-tree-2026-09-15).
The read model it publishes into is
described in [Wiki Tree, Rendering & Per-Doc History](../../system/contracts/wiki-tree.md);
the editing and branch policy it deliberately does not change is in
[Wiki editing and branch flow](../../start/wiki-editing-and-branch-flow.md).

## What is published

The published Wiki revision is **one commit**, resolved from an accepted
integration branch or a release revision. It is never an active task branch.

| Setting | Meaning |
|---|---|
| Project setting `wikiSourceBranch` | The source ref, for example `origin/develop`. Set per project in Project Settings or through the registry API. |
| Accepted ref patterns | `main`, `master`, `develop`, `release/*`, a release tag such as `v2026.9.1`, or an explicit 40-character commit SHA. Override with `WikiPublication:AcceptedRefs`. |
| Rejected refs | Everything else, including `task/*`, `runner/*`, and `docs/wiki-edit/*`. The attempt fails with `UnacceptedRef` and the previous revision stays online. |

Pinning a full commit SHA publishes exactly that release revision and never
advances on its own. That is the supported way to freeze the hosted Wiki during
an incident.

## Freshness SLO

**An accepted documentation change is visible at the hosted URL within five
minutes of landing on the source ref.**

The budget is held by the sync interval plus one fetch and one promotion:

| Step | Budget |
|---|---|
| Scheduled tick (`WikiPublication:IntervalSeconds`, default 120 s) | up to 120 s |
| `git fetch` of the configured remote | up to 30 s (bounded process timeout) |
| Materialize `docs/` at the candidate commit and rebuild the Wiki cache | seconds, proportional to tree size |

The configuration reader clamps the interval to 15 s to 240 s, so a
misconfigured value cannot silently widen the SLO past the five-minute budget.

## Configuration

```jsonc
// appsettings.<Environment>.json on the hosting server
{
  "WikiPublication": {
    "Enabled": true,          // off by default; dev seats and the supervisor seat never fetch for the Wiki
    "Remote": "origin",
    "IntervalSeconds": 120,   // clamped to [15, 240]
    "SnapshotRetention": 3,   // published + previous are never pruned
    "AcceptedRefs": ["main", "develop", "release/*"]
  }
}
```

`Enabled` governs the scheduled trigger only. With it false the host never
fetches for the Wiki on a timer, which is why dev seats and the supervisor seat
leave it off; the operator endpoints still work, so a one-off publication on
such a host is possible and deliberate. A project without `wikiSourceBranch`
stays checkout-backed and is never fetched for at all.

## Credentials

Publication performs exactly one network operation: `git fetch --prune <remote>`
in the deployment checkout. It never pushes and never writes to the repository.

- Use a **read-only** deployment credential. A read-only HTTPS token or an
  SSH deploy key without write access is sufficient and is the intended
  configuration.
- Store it where the service user's Git can find it non-interactively (SSH
  agent-less deploy key in `~/.ssh/`, or a credential helper). The backend
  never prompts, so a missing credential surfaces as a `FetchFailed` outcome
  rather than a hung process.
- The Task Server's own authentication is unchanged. Hosted Wiki publication is
  not anonymous publication: reaching the hosted origin still requires a Studio
  session, exactly as every other route does.

## Triggers

| Trigger | How |
|---|---|
| Scheduled | `WikiPublicationSyncService` ticks every `IntervalSeconds`. This is the normal path. |
| Operator, forced | `POST /api/projects/{projectName}/wiki/publication/sync` |
| Operator, rollback | `POST /api/projects/{projectName}/wiki/publication/rollback` |

Both POSTs run the same service the scheduled tick runs, so a forced sync can
never take a different path than the timer. Both are mutations and therefore
require the `X-Client-Id` header like every other mutating route.

**A rollback pins the project.** After a rollback the scheduled tick reports
`Held` and deliberately does not advance, because otherwise the next tick would
re-publish exactly the revision the operator just removed. `held: true` in the
diagnostics report is the visible state. A forced sync is what releases the pin
and resumes scheduled publication, which is why it is the last step of the
rollback drill below.

## Deployment evidence

```bash
curl -fsS -H 'X-Client-Id: local-default' \
  "$origin/api/projects/Agent%20Studio/wiki/publication"
```

```jsonc
{
  "projectName": "Agent Studio",
  "sourceRef": "origin/develop",
  "enabled": true,
  "publishedSha": "8d10db4e...",
  "publishedShortSha": "8d10db4e",
  "publishedAtUtc": "2026-09-15T08:41:02Z",
  "previousSha": "4c21aa90...",
  "rollbackAvailable": true,
  "lastOutcome": { "status": "Promoted", "failure": "None", "fromSha": "4c21aa90...", "toSha": "8d10db4e..." },
  "lastFailure": null,
  "intervalSeconds": 120
}
```

The same promotion is recorded in the backend log as
`wiki-publication-promoted project=<id> ref=<ref> from=<sha> to=<sha> trigger=<trigger>`,
so old and new SHAs are in the run evidence as well as in the API.

## Atomicity and what a reader sees

1. The candidate commit's `docs/` tree is extracted into a staging directory and
   published into its SHA-addressed snapshot folder with one directory move. A
   process that dies mid-extract leaves an abandoned staging folder, never a
   half-populated snapshot.
2. Only after that snapshot validates does the promotion swap a single
   immutable published-revision reference.
3. The Wiki cache is refreshed after the swap.

A concurrent reader therefore sees either the whole previous tree or the whole
new one; ADR-0074 records why that guarantee is a property of the single
reference swap rather than of any filesystem operation. A reader that arrives
between the swap and the cache rebuild sees a complete previous-generation
tree, never a missing or mixed one. Tree, page,
assets, history, search, Pulse, and revision preview all resolve against the
published commit rather than re-resolving the ref per request, so the surface
stays single-valued while the source branch moves on.

## Write behaviour

A published Wiki is branch-backed and therefore read-only. Page save, create,
move, delete, upload, classification, ordering, and Home curation are rejected
with the existing divergence-prevention message. A hosted read session cannot
silently commit into the deployment checkout. Editing remains governed by the
documented branch flow: edit in a checkout-backed seat, get the change accepted
onto the integration branch, and the hosted Wiki picks it up on the next tick.

## Failure recovery

Every unsuccessful attempt leaves the previous published revision online and
records a typed failure. The forced-sync endpoint answers `409` with that
outcome; the service keeps serving.

| Typed failure | Meaning | Recovery |
|---|---|---|
| `RepositoryUnavailable` | The project has no resolvable repository checkout on this host. | Fix `repositoryPath` in the project registry, or restore the deployment checkout. |
| `UnacceptedRef` | `wikiSourceBranch` is not an accepted integration branch or release revision. | Point the project at the integration branch, or extend `WikiPublication:AcceptedRefs` deliberately. |
| `FetchFailed` | The remote could not be contacted, or the deployment credential is missing or expired. | Check the credential and network reachability, then force a sync. Nothing changed on the hosted side. |
| `RevisionNotFound` | The ref does not resolve to a commit in this repository. | Verify the ref exists on the remote and that `--prune` did not remove a renamed branch. |
| `SnapshotFailed` | The revision resolved but its `docs/` tree could not be materialized. | Check free disk space under the system temp directory, then force a sync. The previous revision is still online. |
| `RollbackUnavailable` | A rollback was requested but no previous revision is still readable on this host. A second consecutive rollback also lands here: only one generation is retained, so rollback never silently rolls forward. | Pin `wikiSourceBranch` to the known-good commit SHA and force a sync. |

### Rollback drill

Rehearse this on the hosting server after every change to the publication
configuration:

```bash
origin=https://studio.example.com
project=Agent%20Studio
auth=(-H 'X-Client-Id: local-default')

# 1. Record the revision that is online.
curl -fsS "${auth[@]}" "$origin/api/projects/$project/wiki/publication"

# 2. Roll back to the previous published revision.
curl -fsS "${auth[@]}" -X POST "$origin/api/projects/$project/wiki/publication/rollback"

# 3. Confirm the hosted tree serves the earlier commit.
curl -fsS "${auth[@]}" "$origin/api/projects/$project/wiki/tree" | head -c 400

# 4. Release the hold and return to the accepted revision.
curl -fsS "${auth[@]}" -X POST "$origin/api/projects/$project/wiki/publication/sync"
```

Between steps 2 and 4 the scheduled tick reports `Held` and leaves the restored
revision online. Step 4 is the only thing that resumes it.

Rollback reuses the retained snapshot of the previous revision. It performs no
fetch and no archive work, so it cannot fail on a network problem and completes
in well under the switch budget. Snapshot retention never prunes the published
or the previous revision, which is what keeps this drill available.

If the previous revision is gone (`RollbackUnavailable`), set
`wikiSourceBranch` to the known-good commit SHA and force a sync. That pins the
hosted Wiki to that release revision until the setting is changed back.

## Non-goals

- Anonymous publication. The hosted origin stays authenticated.
- A second, statically rendered Wiki site. There is one read model.
- Syncing unaccepted task branches.
