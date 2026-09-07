# AGT-2417 verification

## Live Research pipeline

- Command: `npx playwright test e2e/task-detail/research-lightweight-pipeline.spec.ts --project=chromium`
- Result: passed, one real Research card completed its Core agent run in 1.2 minutes.
- Route: `gpt-5.6-luna` with `medium` thinking. This is within the model-routing policy for a trivial, mechanical HTML artifact probe with deterministic verification and no correctness floor.
- Pipeline: `read-only-task-pipeline`, displayed as `Lightweight report pipeline`.
- Negative proof: the resolved step list contains no build/test gate, Stylelint, review aspect, code-review grade, Wiki automation, task spawner, regression radar, or drift step.
- Screenshot: [research-lightweight-pipeline.png](research-lightweight-pipeline.png)

## Backend verification

- ProductFailure reproduction: the exact `dotnet build` profile failed in `runner/RemoteTaskRunner.cs`; the pristine `2cc706d99` baseline passed. The missing `integrationBranch` forwarding was repaired with the identical two-line fix already present on `origin/develop`.
- The exact `dotnet test --filter Category!=MachineBound --nologo` profile exposed three new failures. The Research prompt now carries its required review companion, the management assertion tolerates legitimate startup audit events while retaining the seeded-event proof, and structured completion now preserves the scanner-parsed `TASK_DONE` across an operator-requeue log boundary.
- Focused regression run: 3 passed, covering the prompt companion, management audit, and fresh operator-requeue aspect assessment.
- A backend-only focused harness also covers the short pipeline shape, dangling-dependency guard, mode and AGT-2301 type routing, Research versus Planning prompt framing, valid primary-HTML acceptance, and invalid-HTML reissue without aspects.
- Final exact profile: 4,674 passed, 12 skipped, 0 failed out of 4,686 tests in 3 minutes 2 seconds.

## Result viewer boundary

- Stable API reproduction on AGT-2380: `GET /api/tasks/AGT-2380/results/report.html` returned `404`; its existing Result pane is still driven by the historical `status.md`.
- The results endpoint is read-only and currently serves non-image extensions as `application/octet-stream`.
- The missing `results/report.html` primary-selection/sandbox-rendering slice was appended idempotently through the Task API to AGT-2409.
- AGT-2409 is now completed without that viewer slice. AGT-2417 therefore does not claim the AGT-2380 Result-tab screenshot; the convention and report are delivered, and the viewer integration remains the explicitly delegated dependency.
