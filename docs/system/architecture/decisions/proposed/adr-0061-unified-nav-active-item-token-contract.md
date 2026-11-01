# ADR-0061 - The active side-menu item is ONE shared token contract (`--studio-nav-active-*`), not one new nav component

**Status.** Proposed. On acceptance it folds into [adr-archive.md](../adr-archive.md) as ADR-0061.

**Date.** 2026-07-10.

**Scope.** AGT-2010 (Navigation vereinheitlichen). Operator directive (Robert, 2026-07-09, with screenshot evidence across Project Hub + Prompt catalogue): active navigation entries were only *subtly* recognisable everywhere; the ask is a **colour-filled active menu entry** that reads "immediately and very fast", and **one** shared visual concept for all these side menus.

---

## 1. Context

The studio has one persistent-list, one-active-item side menu at several levels:

| Level | Surface | Component |
|---|---|---|
| L1 | Explorer workspace tree | `features/studio-shell/explorer-workspace-tree` (`<app-tree-row>`) |
| L2 | Project Hub rail (INSIGHT / QUALITY / CONTEXT / CONFIG) | `features/project-detail/project-shell` (`<app-tree-row>`) |
| L3 | Prompt catalogue (RUNNER / REVIEW / …) | `features/orchestrator/prompt-admin-panel` (`<app-tree-row>`) |
| S1/S2 | Orchestrator + Workspace settings rails | `orchestrator-settings-modal`, `shell/workspace-overlays` |
| W1/W2 | Wiki tree, Agent-Docs tree | `project-wiki-section`, `project-steering-docs-section` |
| T1/C1/N1 | Open-tabs list and focused-view task rail | `components/list-row`, root `app.scss` |

The pre-analysis verdict ("kein geteiltes Komponenten-/Zustandsmuster") was **half right**: L1/L2/L3 already share the *same* Angular components (`<app-tree-row>` + `<app-section-header>` + `<app-count-badge>`) from a prior consolidation slice — so the *component* was already unified. What was **not** unified was the **active-state look**: the shared tree-row painted a near-grey 14% tint, the settings rails used a plain grey `bg-selected` with no accent, and two rails (CLI sessions, task rail) hard-coded **off-brand colours that ignored the theme** (blue `rgba(96,165,250,…)`, indigo `rgba(99,102,241,…)`). That is the inconsistency the operator saw.

## 2. Decision

Make the active side-menu-item treatment **one token contract**, and re-point every side menu at it — rather than introducing a new `NavList` / `NavSection` / `NavItem` component set.

Four semantic tokens in `src/styles/_tokens-semantic.scss` are the single source of truth, defined for both themes:

```
--studio-nav-active-bg        // clearly accent-tinted band (dark 30% / light 26%)
--studio-nav-active-bg-hover  // distinct hover-on-active
--studio-nav-active-fg        // strong foreground text (flips per theme)
--studio-nav-active-bar       // bright accent side bar (brand orange)
```

Every side menu paints its active row from these tokens: an accent band, an `inset 3px 0 0 0 var(--studio-nav-active-bar)` side bar on flush rails (or a strengthened accent border on card-shaped rows), a `:focus-visible` accent ring for keyboard nav, and `aria-current="page"`. Hover stays a plain `bg-hover` wash so it reads clearly weaker than active. Because L1/L2/L3 already share `<app-tree-row>`, the strong active state landed there once and propagates to all three at once; the bespoke rails (S1/S2/W1/W2/T1/C1/N1) were re-pointed at the same tokens, removing the two off-brand hard-coded colours.

## 3. Reasoning style — why a token contract, not a new component set

The literal brief said "build a NavList / NavSection / NavItem set". We deliberately reinterpreted "one unified nav system" as a **token contract over the components that already exist**, because:

- **The components were already shared.** L1/L2/L3 already render through `<app-tree-row>` + `<app-section-header>` + `<app-count-badge>`. Introducing a parallel `NavItem` set would mean *re-migrating* three rails onto a second component that renders the same markup — churn with no behavioural gain, and a window where two "canonical" nav components coexist.
- **The defect was the *look*, not the *structure*.** The operator's pain is the subtle active state and the off-brand colours. A token contract fixes exactly that, in one place, for every rail — including the bespoke rails that are **not** tree-rows and never would have adopted a `NavItem` component cheaply (the CLI-session `<button class="session">`, the settings `<button class="ws-settings__rail-item">`).
- **"Same components = stable and future-proof" is satisfied by the token, not the class.** Any future rail — tree-row or bespoke — becomes consistent by consuming `--studio-nav-active-*`. The token is the smaller, more universal contract; it binds rails that a shared component could not reach.

Trade-off accepted: there is no compile-time guarantee a *new* rail consumes the tokens (a `NavItem` component could enforce that by construction). We accept this because the token set + the `frontend/AGENTS.md` "Navigation active-item" contract + the tree-row spec guard are the enforcement surface, and because the alternative's migration cost and dual-canonical-component risk outweigh the type-safety it would buy.

## 4. Consequences

- One edit to `--studio-nav-active-*` restyles the active state of **every** side menu in both themes.
- Two off-brand, non-theme-aware colours (CLI-session blue, task-rail indigo) are gone; both rails now flip correctly between themes.
- The canonical nav item stays `<app-tree-row>`; the contract for the rest is "read the tokens", documented in [`frontend/AGENTS.md`](../../../frontend/AGENTS.md) → *Navigation active-item: one shared token contract*.
- No new component, no new public surface, no API/CLI/task-contract change — an internal, theme-correct styling consolidation.

## 5. Acceptance

- `frontend/src/app/components/tree-row/tree-row.component.spec.ts` — guards the shared contract: the `active` input drives `.tree-row--active` and the caller's `aria-current="page"` lands on the row button (3/3 green via `ng test`).
- `npm run lint:scss` green (0 errors).
- Before/after evidence per level (both themes), reproducible from the committed harness `frontend/e2e/visual-evidence/nav-active-state.harness.mjs`, which compiles the **real** tokens + component SCSS and re-applies the verbatim pre-change active rules for the BEFORE panel.
