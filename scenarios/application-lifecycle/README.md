# Local Click Counter scenarios

From a prepared checkout with Playwright Chromium installed, run either command at the repository root.

```sh
scenarios/application-lifecycle/run.sh lifecycle
scenarios/application-lifecycle/run.sh product
```

The commands reuse the Debug solution and prebuilt frontend, building missing assets once. Each starts the Production networked profile on the disposable AGT-4239 startup fixture and enrolls its own identities. The product scenario deploys its own baseline so it can run independently. The backend, edge, coding runner, Git origin and loopback websites are scoped to that run.

Both commands write screenshots and UTC step evidence under `JOB_RESULTS_DIR` (default `artifacts/scenarios`). Each command replaces its own generated subdirectory (`lifecycle/` or `product/`) before building or starting Studio. The combined `report.md` shows the latest attempt for each scenario, its exit status and its screenshots. Commands sharing a results directory run one at a time. An unmet scenario assertion exits nonzero even when later independent steps pass.

The [Dossier](../../docs/operations/application-lifecycle/index.html) defines the lifecycle and product-team steps in sections 8 and 18. The fixture follows the [product scenario](../../docs/operations/testing/product-scenario.md) for invitations, chat, preview, feedback, acceptance and permission checks. The local gate uses the release receipt and deploy-job protocol of the repository's deploy lifecycle browser test. It measures health against the bytes Studio supplied for the claimed job. A release exercised independently of a failed card is identified in the evidence.

Run `node --test scenarios/application-lifecycle/scenario.test.mjs` to check completion polling and evidence replacement without starting Studio.
