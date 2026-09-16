# Stale-branch sweep

The sweep classifies **every** remote ref of this project's repository through the one
shared retention policy, reports what it found, and reclaims only what that policy
allows. It never decides by folder timestamps or branch names alone.

## Classes

| Class | Refs | Deleted when | Default window |
|---|---|---|---|
| `task` | `task/<key>` | Contained in both `develop` and `main`, or abandoned (see below) | 7 days |
| `runner` | `runner/<runner>/<key>` | Same as `task` | 7 days |
| `delivery` | `delivery/<key>` | Same as `task` | 7 days |
| `results` | `agent-studio/results/...` | The delivery proof is contained in `main` | none (containment only) |
| `salvage` | `agent-studio/salvage/...` | In `main`, or older than its window and not referenced by an open card | 14 days |
| `quarantine` | `agent-studio/quarantine/...` | Older than its window and not referenced by an open card | 30 days |
| `protected` | `main`, `develop`, `release/*`, `v*` | Never | - |
| `unmanaged` | Everything else | Never | - |

An unmerged `task`, `runner`, or `delivery` ref is **abandoned** once its card is
archived or no longer exists, no open card references it, and its tip is older than the
abandoned window (90 days by default). That is the only rule that removes work which
never reached the integration line. A ref checked out in a live worktree is never a
candidate.

## Modes

- **report-only** (default) - the scheduled run classifies and writes a report. It
  deletes nothing.
- **reclaim** - the scheduled run also deletes what the policy allows.

Confirm-and-execute from this panel works in either mode; it re-checks eligibility and
the branch tip immediately before each delete, so a ref that moved since the report is
kept instead of truncated. Bulk deletions go out in batches of at most 100 refs per
push and can be stopped between batches.

## Reports

Every run writes `reports/branch-sweep/<project>/<timestamp>.json` and a short
`.md` summary under the workspace root. Read one before switching a project to
`reclaim`.
