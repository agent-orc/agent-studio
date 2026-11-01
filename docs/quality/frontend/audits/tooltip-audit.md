# UI Tooltip Audit & Canonical Standard

Last sweep: 2026-06-09.

## The canonical pattern

Every tooltip in the agent-taskboard frontend goes through one directive:

```html
<button [appTooltip]="'Refresh the board'">↻</button>
<button [appTooltip]="{ title: 'Refresh', body: 'Re-fetches all lanes from <code>/api/tasks</code>.' }">↻</button>
<span [appTooltip]="row.tooltip"
      tooltipPosition="top"
      tooltipSeverity="warn">…</span>
```

Source: [`frontend/src/app/components/tooltip/`](../../../frontend/src/app/components/tooltip).

| Capability | Behaviour |
|------------|-----------|
| Trigger | Instant mouseenter, focusin, touchstart. No 500ms native delay. |
| Dismissal | mouseleave, focusout, click, document-level touch (touch sticks 3s by default). |
| Body | Sanitised HTML (DOMPurify). Plain string, `{title, body}` object, or `null` to disable. |
| Severity | `info` / `warn` / `error` / `success` colour the border + title. |
| Position | `top` / `bottom` / `left` / `right` / `auto` (default). Auto picks the first side that fits, then clamps to the viewport. |
| Rendering | Single shared `position: fixed` DOM node, lazily created on first hover. No initial-render cost. |
| A11y | `role="tooltip"`. Shows on focus + reduced-motion respects `prefers-reduced-motion`. |
| Touch | Tap shows for 3s; document-level tap elsewhere dismisses. |

Allowed HTML tags inside the body (DOMPurify allow-list):
`b, strong, i, em, u, code, kbd, br, p, small, ul, ol, li, table, thead, tbody, tr, th, td, span, div`.
Only the `class` attribute survives sanitisation; nothing else is honoured.

## Hard rules

1. Use `[appTooltip]` everywhere a browser-native tooltip would otherwise appear. No native `title=""`, no `[title]=""`, no `[attr.title]=""` on DOM elements. Angular component inputs named `title` are allowed when they render visible headings, for example `<app-dialog title="...">` or `<app-section-header title="...">`.
2. No new custom tooltip components or `@HostListener('mouseenter')` popovers. If a richer interaction is needed (modal, hover-panel, command palette), that is a different component, documented separately. Out of scope for the tooltip standard: drag-and-drop hints, popover/modal-like inline panels (e.g. `features/tokens/components/usage-hover-panel.ts`).
3. Severity is a visual highlight, not a substitute for content. A warn-coloured tooltip still needs a clear body string.
4. Body content is HTML-safe by virtue of DOMPurify; do not bypass it with raw `innerHTML` assignments.

## Migration sweep (2026-06-08)

The migration scripts under `frontend/scripts/`:

- [`migrate-title-to-tooltip.mjs`](../../../../frontend/scripts/migrate-title-to-tooltip.mjs) - rewrites `title=`, `[title]=`, `[attr.title]=`, and `[appTip]` to `[appTooltip]`.
- [`inject-tooltip-import.mjs`](../../../../frontend/scripts/inject-tooltip-import.mjs) - injects `TooltipDirective` into each standalone component's `imports:` array (creating the array if absent).

After the current sweep, tooltip behaviour renders through the canonical directive. Counts:

| Surface | Files touched | Tooltip sites |
|---------|---------------|---------------|
| `frontend/src/app/` | 59+ templates | 200+ tooltip bindings |

### Migrated tooltip sites (compact)

The list is grouped by feature. Each row is `<file> - <count>`. Anything not in the list either had no tooltip surface or is intentionally out of scope.

#### App shell + global

- `app.html` - 7
- `components/chat/chat.component.html` - 2
- `components/chat/conversation-view.component.html` - 4
- `components/concept-help/concept-help.component.html` - 1
- `components/info-button/info-button.component.html` - 1
- `components/markdown-rich-editor.html` - 7
- `features/shell/components/status-bar.html` - 10
- `features/shell/components/workspace-overlays.component.html` - 3
- `features/shell/components/auto-review-indicator.html` - 1

#### Board + lanes

- `features/board/components/board-search-icon/board-search-icon.component.html` - 2
- `features/board/components/create-job-dialog/create-job-dialog.component.html` - 6
- `features/board/components/job-card/job-card.component.html` - 13
- `features/board/components/job-column.html` - 4
- `features/board/components/kanban-filter-sidesheet/kanban-filter-sidesheet.component.html` - 3
- `features/board/components/project-tabs/project-tabs.component.html` - 3

#### Job detail

- `features/job-detail/job-detail.html` - 2
- `features/job-detail/components/activity-log-view.html` - 3
- `features/job-detail/components/command-deck/command-deck.component.html` - 4
- `features/job-detail/components/detail-header/detail-header.component.html` - 7
- `features/job-detail/components/git-pane/git-pane.component.html` - 7
- `features/job-detail/components/git-pane/git-file-tree.component.html` - 1
- `features/job-detail/components/hygiene-strip/hygiene-strip.component.html` - 3
- `features/job-detail/components/hygiene-strip/project-hygiene-badge.component.html` - 1
- `features/job-detail/components/log-overlay/log-overlay.component.html` - 1
- `features/job-detail/components/pane-toggle-bar/pane-toggle-bar.component.html` - 4
- `features/job-detail/components/prompt-pane/prompt-pane.component.html` - 2
- `features/job-detail/components/protocol-pane/code-review-panel.component.html` - 1
- `features/job-detail/components/protocol-pane/protocol-pane.component.html` - 20
- `features/job-detail/components/protocol-pane/review-evidence-panel.component.html` - 2
- `features/job-detail/components/protocol-pane/run-timeline.component.html` - 5
- `features/job-detail/components/triage-panel/triage-panel.component.html` - 1

#### Orchestrator + supervisor

- `features/orchestrator/components/global-orchestrator-card.html` - 1
- `features/orchestrator/components/orchestrator-feed.html` - 2
- `features/orchestrator/components/orchestrator-side-sheet/orchestrator-side-sheet.component.html` - 6

#### Project chat + project detail

- `features/project-chat/components/project-chat-list/project-chat-list.component.html` - 4
- `features/project-chat/components/project-chat-rail/project-chat-rail.component.html` - 1
- `features/project-detail/components/autonomy-slider.html` - 1
- `features/project-detail/components/project-analysis-reports-section.html` - 4
- `features/project-detail/components/project-detail.html` - 4
- `features/project-detail/components/project-drift-overview-section.html` - 3
- `features/project-detail/components/project-drift-section.html` - 2
- `features/project-detail/components/project-observability/project-observability-panel.component.html` - 2
- `features/project-detail/components/project-overlays.component.html` - 2
- `features/project-detail/components/project-steering-docs-section.html` - 1
- `features/project-token-usage/components/project-token-usage-panel.component.html` - 2

#### CLI, quota, tokens

- `features/cli/components/cli-usage-sheet.html` - 1
- `features/quota/components/header-quota.html` - 2
- `features/quota/components/quota-strip.html` - 3
- `features/tokens/components/cli-usage-detail-modal.html` - 5
- `features/tokens/components/token-summary-block.html` - 4

#### Dev tools, screenshots, update, verbose debug, workforce

- `features/dev-tools/components/e2e-cleanup-dialog.component.html` - 1
- `features/dev-tools/components/update-stable-console.component.html` - 1
- `features/screenshots/components/screenshot-strip/screenshot-strip.component.html` - 2
- `features/update/components/update-center/update-center.component.html` - 1
- `features/update/components/update-version-badge/update-version-badge.component.html` - 1
- `features/verbose-debug/components/verbose-debug-overlay.component.html` - 2
- `features/workforce/components/phase-summary-list/phase-summary-list.component.html` - 1
- `features/workforce/components/role-badge/role-badge.component.html` - 1

### 2026-06-08 finish sweep

These concrete native-title or divergent tooltip sites were rechecked or migrated:

| Site | Previous pattern | Current pattern |
|------|------------------|-----------------|
| `components/info-button/info-button.component.html` | Lane help trigger tooltip | `[appTooltip]="label()"` |
| `components/cli-model-selector/cli-model-selector.component.html` | Picker chip hint | `[appTooltip]="tooltip()"` |
| `components/chat/chat/chat.component.html` | Attachment/action hints | `[appTooltip]` on icon buttons |
| `components/chat/conversation-session-card/conversation-session-card.component.html` | Session id/raw-line hints | `[appTooltip]` |
| `features/task-detail/components/detail-header/detail-header.component.html` | Title/id/action hints | `[appTooltip]` |
| `features/task-detail/components/activity-log-view/activity-log-view.html` | Activity action/duration hints | `[appTooltip]` |
| `features/task-detail/components/prompt-pane/overview-pane/overview-pane.component.html` | Pipeline row/token/cost hints | `[appTooltip]` and component-title inputs only |
| `features/task-detail/components/prompt-pane/agent-work-detail/agent-work-detail.component.html` | Tool-call argument/result hints | HTML `[appTooltip]` |
| `features/task-detail/components/hygiene-strip/hygiene-strip/hygiene-strip.component.html` | Commit/tree health hints | `[appTooltip]` |
| `features/dev-tools/components/tag-manager-dialog/tag-manager-dialog.component.html` | Native `[title]` on tag id/description | `[appTooltip]` |
| `components/markdown-utils.ts` | Generated link/image `title` attributes | Removed native tooltip attributes from rendered Markdown |
| `features/task-detail/components/beautiful-results/beautiful-results.renderer.ts` | Generated link/image `title` attributes | Removed native tooltip attributes from rendered results |

Verification grep for native DOM-title patterns:

```powershell
rg --pcre2 -n "<(?!app-|ng-|mat-|cdk-)[a-z][\\w-]*(?=[^>]*(?:\\s(?:title|\\[title\\]|\\[attr\\.title\\])\\s*=))|` title=\\`\\\"" frontend/src/app -S
```

This grep intentionally allows component inputs named `title`.

### 2026-06-09 residual native-title sweep

These residual native DOM-title sites were removed after the canonical standard landed:

| Site | Previous pattern | Current pattern |
|------|------------------|-----------------|
| `components/media-lightbox/media-lightbox.component.html` | Native `[attr.title]` on the zoomable image | `[appTooltip]` with English zoom-state text |
| `components/chat/tool-burst-chip/tool-burst-chip.component.html` | Native `[title]` on command and source-hit text | `[appTooltip]` on both truncated text hosts |

Visual regression coverage now lives at [`frontend/e2e/board/tooltip-standard.spec.ts`](../../../../frontend/e2e/board/tooltip-standard.spec.ts). It verifies lazy singleton creation, instant hover visibility, no native `title` attributes on the sampled hosts, and screenshot evidence for board-control, status-bar, and structured commit tooltips.

## Drift enforcement

A `tooltip-canonical-directive` rule lives in [`docs/system/contracts/code-patterns.md`](../../../system/contracts/code-patterns.md). The deterministic [`CodePatternDriftAnalysisService`](../../../backend/Services/Drift/CodePatternDriftAnalysisService.cs) will flag any new `title=`, `[title]=`, `[attr.title]=`, or `[appTip]` usage under `frontend/src/app/` until the offender is rewritten to `[appTooltip]`.

The Vitest suite at [`tooltip.directive.spec.ts`](../../../frontend/src/app/components/tooltip/tooltip.directive.spec.ts) covers the instant trigger, lazy-render, sanitisation, focus + touch, severity class, viewport placement, and `position: fixed` invariants. The Playwright spec at [`frontend/e2e/board/tooltip-standard.spec.ts`](../../../../frontend/e2e/board/tooltip-standard.spec.ts) covers the live-browser visual contract.
