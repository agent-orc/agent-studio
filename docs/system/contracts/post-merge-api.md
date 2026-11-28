# After-merge host API contract

Studio serves these routes with camel-case JSON. On networked installations,
use HTTPS and a Runner service bearer with the scope listed per route. The
server derives the Runner identity from the authenticated principal. The worker
sends `X-Post-Merge-Instance` with the instance ID from its Runner registration
on discovery and all subsequent requests. Studio checks that registration and
the project's binding before offering custody.

The coding role advertises `coding:post-merge` with status `ready` or
`disabled`. Claim admission requires a fresh `ready` advertisement, the
coding executor role, and open host admission. The Studio host view displays
this capability as **After-merge work**. See the
[coding role setting and rollout](../../operations/setup/linux-runner-host.md#post-merge-develop-checks).

## Discovery and custody

The Runner routes below use the prefix `/api/runner/post-merge/projects`.
Use the returned stable `projectId` as `{project}`.

| Method and suffix | Runner scope | Request and response |
|---|---|---|
| `GET` (prefix itself) | `runner.claim` | No body. `200` with an array of `{projectId, project, repositoryUrl, branch}`, where `project` is the display name. Only bound, non-archived fast or balanced projects with a repository locator are offered. An ineligible coding instance receives an empty array. |
| `POST /{project}/claim` | `runner.claim` | Send `{repositoryUrl, branch, readTip, occupiedSlots, loadPerCore, recoveryOwner?}`. The locator and branch must match discovery, `readTip` is the 40-hex commit from a successful authenticated branch probe, `occupiedSlots` is a nonnegative integer, and `loadPerCore` is a nonnegative number or null. Supply `recoveryOwner` only when resuming a local receipt journal. `200` returns `{id, expiresAtUtc, placementOnly}`. `204` means no grant, including contention or failed admission. `404` means the project is unavailable to the caller or has no eligible target. |
| `POST /{project}/grant` | `runner.lease` | No body. `200` returns the same grant ID and permissions with a renewed expiry. A stale or missing grant returns `409`. |
| `POST /{project}/grant?release=true` | `runner.lease` | No body. `204` releases the current grant or acknowledges a stale or repeated release without affecting its successor. |
| `GET /{project}` | `runner.claim` | `200` returns `PostMergeProjectState`, including pending window, active and historical receipts, test selection, quarantines and holds. A coding instance without current custody receives `409`. |

Studio validates the probe fields against the registered target. The coding host
performs the authenticated Git probe with its own repository credentials. After
a grant, send its ID in `X-Post-Merge-Grant` alongside the instance header.
The grant is exclusive to the project, authenticated Runner and instance. It
expires after three minutes, and the daemon renews it every minute while it
owns the project. Treat stale custody as lost and stop using its ID. Keep the
ID out of logs and UI responses. A repeated claim for a live grant by the same
eligible instance preserves the ID and permissions. A drain stops new claims.
The current owner may finish its work and release the grant.

With free coding capacity the server can grant execution custody. When coding
capacity is unavailable and a connected post-merge gate is available, Studio
can issue `placementOnly: true` so the owner can observe and queue work.
Receipts and closures from that grant receive `409`. If gate availability
changes before placement, the holder defers local execution and releases the
grant. Older stored grants without `placementOnly` load with the execution
permissions they already had.

## Window updates and run history

Coding calls to the routes below require the current grant and both headers.
An out-of-binding project returns `404`. Missing or stale custody returns
`409`. The existing gate worker uses its assigned gate attempt for state
writes. Trusted local manual execution retains its existing admission.

| Method and suffix | Runner scope | Request and response |
|---|---|---|
| `POST /{project}/window` | `runner.completion` | `{tipSha, openedAtUtc, observedAtUtc, windowMinutes, admissionReason}`. The opening time and admission reason can be null. `204` records the observation, `400` rejects invalid values. |
| `POST /{project}/placement?kind=window` | `runner.completion` | Same observation body. `kind` is `window` by default or `nightly`. `200` returns `{executor, reason, subjectId}`. The executor is `gate` for a queued or existing gate subject, `run` for coding fallback, or `closed` for a closed obligation. The subject ID is set for gate placement. Invalid tip or kind returns `400`. |
| `POST /{project}/close-window` | `runner.completion` | `{tipSha, closedAtUtc, reason}`. `204` closes the matching pending tip. A stale tip or invalid reason returns `409`. |
| `POST /{project}/escalation-check` | `runner.completion` | No body. `204` acknowledges the overdue-window check. |
| `POST /{project}/runs` | `runner.completion` | A `PostMergeRunRecord`. Existing `X-Post-Merge-Owner`, `X-Post-Merge-Fence`, and renew/recover headers still apply to exact-tip receipt custody. `200` returns project state after recording, or custody for renewal or recovery. `400` marks invalid evidence or headers and `409` marks stale or competing custody. For coding grants Studio records the registered host ID as `executionHost`. |

Studio's project read `GET /api/projects/{project}/post-merge-runs` requires
project read access. It returns `200` with newest-first summaries
`{id, kind, status, tipSha, startedAtUtc, executionHost}`, including the active
run when present. Older receipts can have null `executionHost`. An
unavailable, archived or inaccessible project returns `404`.
