# Studio header zones

This contract covers the global Studio top bar and every task view element from
the tab strip through the pane toggle bar. It records the current slots rather
than redesigning the headers. The task template order is guarded by
`TASK_HEADER_SLOTS` in `frontend/src/app/features/task-detail/task-header-slots.ts`.
The tab strip and task toolbar are in the Studio shell and its projected tab
actions; the global alert is projected from `app.html` before `app-task-detail`.

## Fixed order and fact ownership

| Level and order | Slot | Owned fact or action | Render condition | Treatment and origin |
|---|---|---|---|---|
| App 1 | Navigation | Home and primary destinations | Studio shell open | Single top row, text-only More menu below inline breakpoints; `d6b11df5d`, AGT-2963 (`3de9c213b1`) responsive correction. |
| App 2 | Workspace breadcrumb and project picker | Active workspace, project, auto mode, open task count | Workspace/project known; picker always | Breadcrumb text and one picker pill; `d6b11df5d`. |
| App 3 | Search | Global search action | Studio shell open | Header button; `5962926653`. |
| App 4 | Task defaults, Environment, Project Chat | Defaults and workspace tools | Capabilities available | Shared header buttons; defaults `7fb0177069`, Environment `958fbf2411`, chat `8e1228f292`. |
| App 5 | Theme, Needs you, Settings, account | User actions and the operator attention count | Needs you always; its count takes the warning tint only when nonzero | Icon or text buttons plus account control; theme `d6b11df5d`, settings/account `7fb0177069`, AGT-3356 moved usage to footer, AGT-3576 replaced the stall bell with `app-attention-control`. |
| Task 1 | Tab strip | Open tabs and active task identity | Editor open | One row; `d6b11df5d`, AGT-2963 responsive fix. |
| Task 2 | Task toolbar | Run activity, execution host, pane toggles, lane pager, paging lane, primary action, menu | Task tab active; individual controls follow their capability | One action row; `ccc0039eb` (AGT-2819 extraction). Host belongs here and nowhere in the Overview title. |
| Task 3 | Global alert banner | Cross-task integration alert | Alert threshold reached | Single alert banner in `app-workspace-banner`; `5d496e73e`. |
| Task 3a | Phone primary lane action | Current lane decision | Primary action available below 600 px | Full-width shared primary button above task notices. |
| Task 4 | `app-task-resource-status` | Resource limitation affecting this task | Resource finding exists | Notice; `2b1436acc`. |
| Task 5 | `app-detail-header` | Legacy task identity and actions | Legacy focused view; hidden in Studio | Header row; original detail extraction `839f3c9801`. |
| Task 6 | `app-archived-task-notice` | Archive state and restore | Archived task | Notice; `1bc7b5703`. |
| Task 7 | `app-task-live-status` including `app-pickup-hold` | Current, hold reason and Way out, Next | A meaningful live status exists; hold only for a real hold | Status narrative; AGT-2315 (`029616f74`), hold `e942088b0`, AGT-2378 (`63246e17f6`) board-fresh overlay correction. |
| Task 8 | `app-card-environment-badges` | Task environment facts | At least one relevant environment fact | Compact badges; `4be44bafd`. |
| Task 9 | `app-task-preview` | Explicit task preview | Preview exists | Notice; `53d8fd867`. |
| Task 10 | `app-batch-detail` | Batch verification and panel action | Current task is a batch member in a relevant phase | Notice plus ghost button; `27424297a7`, restricted by AGT-3463. |
| Task 11 | `app-older-brief-offer` | Older brief action | Older brief can be used | Action row; `1e21a6843`. |
| Task 12 | Review round budget | Delivered review round count | Delivered rounds greater than zero | Compact notice; `ccd526925f`. |
| Task 13 | `app-parked-blocker` | Authoritative reason work is parked | Parked blocker exists | Status narrative; `3e0d1f838`, AGT-2816 placement. |
| Task 14 | `app-decision-card-panel` | Decision question and options | This is a decision card | Action panel; AGT-2795 (`f6ba50863`). |
| Task 15 | `app-decision-blocked-notice` | Pending decision blocking this task | Pending decision exists | Notice; AGT-2795 (`f6ba50863`). |
| Task 16 | `app-escalation-summary` | Escalation and recovery actions | Escalated or needs input | Status narrative and action row; `3abe490ad`. |
| Task 17 | `app-command-deck` | Run and stop controls, execution configuration | Task permits an action or has an active run | Action row; `4c73e5ec7`. |
| Task 18 | `app-pane-toggle-bar` | Pane visibility | Task detail open; hidden in Studio where toolbar owns toggles | Toolbar; `170164f07`. |
| Task 19 | Phone pane switcher | Active card detail pane | Task detail open below 600 px | One Overview, Result, or Git pane at a time; full page scroll. |

The global top row's child order is navigation, breadcrumb, project picker,
search, defaults, Environment, Project Chat, theme, Needs you, Settings, and
account. Navigation is always available; the breadcrumb needs an active
workspace, and the project picker and Needs you are always available. Search
and the account/settings controls are buttons. Theme is an icon button.
Needs you (`app-attention-control`, AGT-3576) owns the one "what needs me"
count: it opens the attention list read from `GET /api/operator/attention`,
with open operator facts and Dossier gates, plus backup and credential warnings
from their installation reads. Rows show title, project, age, and reason, and
its count is the number of rows that list renders (R3). Open stalls outside those classes
stay in the Activity feed, which the list links to. Below 720 px the label
hides and the bell icon and count stay. The workspace quota banner follows the top row and renders only
when the workspace cap has an actionable finding (`a98537a193`). Usage totals
belong in the footer (AGT-3356), not this row.

Within task slot 2, the order is run activity (active or relevant run only),
execution host (a concrete execution location), pane toggles (task tab),
previous task, pager position, next task, paging lane select, primary lane
action (only when available), and text-only overflow menu. `studio-tab-actions`
owns these controls (`ccc0039eb`, AGT-2819). The primary action can be Run now
or the current lane's decision; its label comes from triage policy. The host
chip is a factual badge, the pager is a compact action group, and the lane
select is a navigation control. The menu owns secondary task actions.
In Human Review, the primary action uses the fresh task-detail integration
verdict. An integrated or not-applicable delivery offers Accept without waiting
for optional Git provenance; a held action names its delivery reason on hover.
Below 600 px, the same primary action renders full width above the task detail;
the toolbar keeps its secondary menu. The phone pane switcher follows the task
notices and command deck and chooses one pane; selecting a previously hidden
pane reveals it in the shared pane visibility state.

The task lane itself has one canonical name and tone from
`lane-presentation.ts`. The toolbar lane select names the **paging lane**;
the Overview title names the **task's actual lane** when these differ. Do not
copy a host, task state, lane, or action into another slot just to make it more
visible. If the paging lane differs, label the difference explicitly.

## Visibility and treatment

Each block needs an affirmative relevance condition for the current task lane
and phase. A block with no fact or available action renders nothing. Do not
show “does not apply”, “not in a batch”, or a loading placeholder for a block
that may stay empty. A genuine in-progress operation can show its own progress.
The global alert describes cross-task health; it is not repeated as a task
notice. A parked blocker precedes the derived escalation summary.

Use one treatment per class: status narratives use `m.deck-panel-muted`,
notices use `m.deck-panel`, acute alerts use `m.deck-panel-attention`, and
action rows use the shared button vocabulary in [buttons.md](buttons.md).
Use one primary action per group; safe alternatives use secondary, tertiary
actions use ghost, destructive actions use danger. Never wrap an existing panel
in another panel shell. Never add a colored left accent edge. Status indicators
follow [the AGT-3402 indicator contract](../../design/status-indicator.md);
this guide does not define new status tones.

## Size and themes

The app navigation is one row with the `--studio-header-nav-h` 44 px minimum
(48 px on phone via `--studio-header-phone-h`). The tab strip and task toolbar
share the `--studio-tabbar-h` 36 px row height; controls fit within it and keep a common
baseline. Long tab titles truncate inside their tab. The tab list scrolls
horizontally without a visible native scrollbar; it never grows a second row.
The toolbar stays reachable at 1024 px: preserve the active tab and primary
action, compact lower priority labels, and put excess actions in the existing
menu. Never let the toolbar clip its primary action or force page-level
horizontal overflow. Check light and dark theme states, including focus and
hover treatments, with semantic tokens.

## Adding a block

Add the child in the slot order above, register it once in `TASK_HEADER_SLOTS`,
state its precise lane and phase relevance condition here, and extend the
focused component and Playwright coverage. If it carries a fact already owned
by a slot, extend that owner instead of adding a duplicate block.
