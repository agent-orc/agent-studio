# AGT-2301 verification

Date: 2026-07-28

## Scope checked

- Pipeline settings are keyed by `task`, `bug`, `feature`, or `planning`.
- Legacy flat step overrides and order migrate to `task`, `bug`, and `feature`. Planning intentionally starts from its lightweight defaults.
- Runtime consumers resolve the effective settings from the card type and report-only mode before applying step activation, order, model, prompt, condition, gate, review, merge, or push behavior.
- The pipeline settings page presents task type first, keeps On/Off in each row, marks framework-specific steps, and protects the selected type from stale asynchronous responses.
- Planning uses the git-free read-only catalogue. Existing concept mode keeps its dedicated catalogue and uses the task-type settings projection.

The recovered AGT-2301 delivery was ported from commit
`9a5f3597d0618e14b724bff3149a9a14ff137e76`. The final branch targets
`origin/develop` at `f884e457e581b077dd01360c2640b2454ef50ae4`.

## Automated verification

| Check | Result |
|---|---|
| `dotnet test backend.Tests/OrchestratorApi.Tests.csproj --filter 'FullyQualifiedName~Pipeline\|FullyQualifiedName~ProjectSettings\|FullyQualifiedName~MergeIntoDevelop\|FullyQualifiedName~AcceptedIntegration\|FullyQualifiedName~IntegrationPush' --no-build --no-restore` | Passed, 241/241 |
| `npm --prefix frontend run typecheck` | Passed |
| `npm --prefix frontend run test:ci -- --include='src/app/features/project-detail/components/project-pipeline-panel/project-pipeline-panel.component.spec.ts'` | Passed, 3/3 |
| `npm --prefix frontend run lint:ci` | Passed: Angular ESLint, SCSS stylelint, and component folder structure |
| `PW_BASE_URL=http://127.0.0.1:4020 JOB_RESULTS_DIR="$PWD/results/AGT-2301/e2e" npm --prefix frontend run e2e -- e2e/project/pipeline-step-config.spec.ts e2e/project/pipeline-page-evidence.spec.ts --project=chromium` | Passed, 7/7 |
| `git diff --check` | Passed |

`npm --prefix frontend run lint:components` still reports 15 pre-existing
component-size baseline violations on the current develop line. The changed
pipeline panel is no longer among them and remains within its existing baseline.

## Review evidence

- `e2e/pipeline-page/pipeline-page-section-light--mocked.png`: task type is the first control, row-local On/Off is visible, and Angular is marked on the framework-specific stylelint step.
- `e2e/pipeline-page/pipeline-page-full-dark--mocked.png`: dark-theme full-page rendering.
- `e2e/pipeline-step-config/04-bug-type-disabled.png`: bug-only step override persisted as Off while the task override stayed unchanged.
- `e2e/playwright/index.json`: final Playwright test inventory and pass status.
