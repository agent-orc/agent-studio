# Status indicator

One shared way to show a state: a tone icon in front of a visible text label.
It exists because every feature built its own status label (about 30
components and several hundred class variants), so a good state and an empty
state often looked the same (operator 2026-11-01, Settings > GitHub >
Repositories, AGT-3402).

This page extends the badge and dot rules of
[R1](style-guide-hard-rules.md#r1---no-left-accent-lines-or-bars) and
[R4](style-guide-hard-rules.md#r4---acute-signals-only-for-acute-states). The
hard rules still win. The full list of other status displays and their
migration plan lives in the
[status display inventory](status-display-inventory.md).

## Component

`<app-status-indicator>` in
`frontend/src/app/components/status-indicator/`.

```html
<app-status-indicator tone="success" label="Connected" />
<app-status-indicator tone="warning" label="Needs confirmation" [detail]="repository.detail" />
<app-status-indicator tone="running" label="Calculating" [compact]="true" />
```

| Input | Type | Meaning |
|---|---|---|
| `tone` | `StatusTone` (required) | `success`, `warning`, `danger`, `info`, `neutral`, `running`. |
| `label` | `string` (required) | The visible state text. Sentence case, never uppercase. |
| `detail` | `string \| null` | Longer explanation shown as a tooltip. A blank detail shows none. With a detail, the indicator becomes focusable so keyboard users reach the tooltip, which is linked by `aria-describedby`. |
| `compact` | `boolean` | Drops the tinted chip and keeps icon plus text, for dense rows, table cells and running text. |
| `testId` | `string` | `data-testid` of the indicator, default `status-indicator`. |

The rendered element carries `data-tone` and `data-icon` so specs and
Playwright can assert the tone without reading colours.

## Tones

| Tone | Icon (`app-studio-icon`) | Token | Use for |
|---|---|---|---|
| `success` | `check` | `--studio-status-success` | The state the operator wants: connected, verified, healthy, active, up to date. |
| `warning` | `warn` (triangle) | `--studio-status-warning` | Works, but needs attention or an operator step: needs confirmation, expiring, behind, degraded, not ready. |
| `danger` | `close` (cross) | `--studio-status-danger` | Failed or blocked right now: missing, unreachable, offline, failed, denied. |
| `info` | `info` (circle with i) | `--studio-status-info` | A neutral fact worth noticing that is not a problem: invited, open enrollment code, read-only access, provider incident with a scheduled retry. |
| `running` | `spinner` | `--studio-status-running` | Work in progress: loading, calculating, querying, updating, verifying. |
| `neutral` | `circle` (empty) | `--studio-status-neutral` | Nothing to report or a settled choice: no repository, not on GitHub, unknown, unused, revoked, disabled, pending checklist step. |

Every tone has its own icon, so the meaning never rests on colour alone. The
icon is `aria-hidden`; the label is the accessible text. Do not hide the label
and do not replace it with the icon.

The six `--studio-status-*` tokens are declared in both theme blocks of
`frontend/src/styles/_tokens-semantic.scss` and alias the existing semantic
pigments (`--studio-success`, `--studio-warn`, `--studio-error`,
`--studio-accent-teal`, `--studio-accent-3-strong`, `--studio-fg-dim`). The
component mixes a quiet tint from the tone over
`--studio-status-chip-surface`, keeping the chip canvas opaque inside tinted
cards. Compact labels use stronger foregrounds in each theme where an
elevated or tinted parent needs them. The running spinner stops under
`prefers-reduced-motion: reduce`; the static spinner icon still differs from
every other tone.

Neutral is deliberately quiet: no tint, a muted border. An empty or settled
state must never compete with a real outcome (R4).

### Contrast on rendered token surfaces

Ratios below are computed from the theme tokens in the rendered page by
`frontend/e2e/system/semantic-contrast.spec.ts`. Normal text must clear 4.5:1;
meaningful dots and icons must clear 3:1.

| Foreground token | Surface token | Light ratio | Dark ratio |
|---|---|---:|---:|
| `--studio-fg-muted` | `--studio-bg-surface` | 6.10:1 | 6.72:1 |
| `--studio-fg-muted` | `--studio-bg-elevated` | 5.16:1 | 5.79:1 |
| `--studio-fg-muted` | `--studio-bg-running` | 4.75:1 | 4.65:1 |
| `--studio-live-stalled-fg` | `--studio-live-stalled-bg` | 4.69:1 | 5.09:1 |
| `--studio-hold-muted-fg` | `--studio-hold-blocked-bg` | 4.65:1 | 4.93:1 |
| `--studio-fg-dim` | pickup hold mechanism tint | 4.96:1 | 4.59:1 |
| `--studio-status-success` | status chip tint | 4.76:1 | 12.66:1 |
| `--studio-status-compact-warning` | `--studio-bg-elevated` | 5.16:1 | 11.37:1 |
| `--studio-accent-warn` dot | `--studio-live-stalled-bg` | 3.79:1 | 6.24:1 |

## Mapping states to tones

Map by what the state means for the operator, not by the colour a legacy badge
used:

| State family | Tone |
|---|---|
| connected, verified, healthy, online, active, ready, available, passed, merged, up to date, matches main, integrated, used (enrollment) | `success` |
| needs confirmation, not installed, expiring, rate limited, behind, degraded, maintenance, not ready, pending dependency, awaiting an operator step, partial, stale | `warning` |
| missing, failed, denied, expired, offline, unreachable, unavailable, unexpected write access, blocked | `danger` |
| invited, open, read-only, provider incident with scheduled retry, diagnosis pending | `info` |
| loading, calculating, querying, updating, verifying, configuring, half-open recovery probe | `running` |
| unknown, none, no repository, not on GitHub, unused, revoked, disabled, retired, idle, draining, pending checklist step, built-in default | `neutral` |

Keep the mapping next to the data it reads, as a typed `Record<State,
{ tone, label }>` or a small pure function with a matrix test, never as a
nested ternary repeated across templates. A state the backend adds later must
fall back to `neutral`.

### Settings mappings (AGT-3402)

| Surface | State source | Mapping |
|---|---|---|
| GitHub > Repositories | `GitHubAppCoverageState` | covered Connected → success; not-installed, not-adopted → warning; no-repository-url, not-github, unknown → neutral. The coverage `detail` is the tooltip. |
| GitHub > Connection, Installations | connected App, confirmed installation | Connected, Confirmed → success; no confirmed installation → warning. |
| Credentials | `CredentialInventoryItem.state`, Codex login | ok → success; expiring, rate limited → warning; missing, expired, unexpected → danger; unused → neutral. |
| Secrets | secret status, backend health | active → success; revoked → neutral; healthy → success; unavailable → danger; built-in store → neutral. |
| Task Server | health, readiness, evidence, maintenance, backups, security, host registry | `healthTone` / `evidenceStateTone` through `indicatorTone()`; pending statistics → running; maintenance normal → success, other modes → warning; backup failure → danger; host registry draining → warning, revoked or retired → neutral. |
| Environment | host and role heartbeat, readiness, provider login, credentials, capability health, Git push, checkpoints, engine, gate, map | `hostStatusIndicatorTone`, `hostReadinessTone`, `providerAuthTone`, `credentialIndicatorTone`, `environmentHealthTone`, `repositoryAccessTone`; live loading → running. |
| Access | user, enrollment code, enrolled host, service account | Active, Used → success; Invited, Open → info; Disabled, Revoked, Expired or revoked → neutral. |
| Updates | update state, deploy history | ok → success; behind → warning; running → running; failed → danger (the text says "Last update failed"); history degraded or aborted → warning. |
| Operations > Security | finding severity, credential, user, host release and audit states (`operations-security.model.ts`) | critical, high → danger; medium → warning; low → info; info → neutral. Active, working, up to date, verified → success; expiring, never expires, no recovery codes, behind → warning; missing, unexpected access, failed, no second factor → danger; expired, revoked, disabled, not visible to the API → neutral. |
| First-run checklist | each step, provider probe, device login, Git setup | Verified → success; Pending → neutral; `providerAuthTone`, `deviceLoginTone`, `gitSetupTone`. |

## When to use it, and when not

Use the indicator for a **state**: something that can change and that the
operator reads to decide whether to act.

Use something else for:

- **Tags and labels** (role names, kinds, provenance, version, plan, scope,
  `API token`): a neutral chip, no tone icon. A tag does not change with the
  system state.
- **Counts** (`3 tasks`, `12 commits`, `N retained` without a state): the
  `<app-count-badge>` or plain tabular text. A count that carries a verdict
  (for example "2 credential alerts") is a state and may use the indicator.
- **Filters and toggles**: interactive controls keep button or segmented
  control semantics. Never put status chrome on a control or button chrome on
  the indicator (living rules, "Labels are not buttons").
- **Lanes**: the lane word and tone come only from `lane-presentation.ts`
  (R9). Do not map a lane onto a status tone.
- **Banners and messages** (`role="alert"` paragraphs, toasts, notices with
  actions): keep the message container. A banner may contain an indicator but
  is not replaced by one.
- **Dense chart marks** (meter fills, heat cells, sparkline points, topology
  edges): keep the mark; put an indicator next to the chart only when the state
  needs words.
- **Model capsules**: follow [model-level-indicator.md](model-level-indicator.md).

## Do not

- Do not add a coloured left edge, stripe or inset shadow to show the state
  (R1). The indicator, a dot or a full-surface tint carry it.
- Do not combine a separate status dot with the indicator; the tone icon is
  the dot.
- Do not uppercase the label or add letter spacing.
- Do not read raw hex or one-theme pigments for a state; add a tone instead.
- Do not keep an acute tone for history: a past failure in a list of finished
  runs is neutral or warning, not danger (R4).
