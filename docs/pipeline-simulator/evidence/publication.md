# Published evidence

Canonical Dossier: [../index.html](../index.html), AGT-4028. The four product
captures below are published beside the Dossier under `assets/`, embedded in
its Evidence section with the canonical full-bleed figure block, and copied
unchanged into the collected task review bundle. They are retained from the
reviewed delivery, not newly captured by this revision. Capture date:
24 November 2026; original source revision: develop `6ad4f5f0e`.

The recorded capture command in the original delivery was:

```sh
npm --prefix frontend run e2e -- e2e/project/project-cycle-time-panel.spec.ts e2e/workspace/token-analysis.spec.ts
```

The throughput spec used a mocked cycle-time response. The token-analysis spec
used its committed deterministic fixture, not the presentation demo store.
The presentation-capture runbook and spec define the pinned-workspace workflow;
these alternate surfaces retain their own explicit fixture provenance rather
than being relabelled as pinned-workspace observations. The original status
reported throughput passing on retry and token analysis passing. This revision
verifies image presence, decoding, dimensions and checksums, and renders all
four Dossier pages with browser readiness and page-error checks. It does not
claim to rerun those product specs. Current document captures are documentation
renders and prove only publication and legibility, not a live simulator.

| Capture | Exact visible fact | Fixture provenance | Size |
|---|---|---|---|
| [throughput-cycle-time-history-only-dark--mocked.png](../assets/throughput-cycle-time-history-only-dark--mocked.png) | Completed-card medians, throughput per UTC day, resource waits and stage table. | `frontend/e2e/project/project-cycle-time-panel.spec.ts`; route mock; panel crop; All time. | 1072 × 1030 |
| [throughput-cycle-time-history-only-light--mocked.png](../assets/throughput-cycle-time-history-only-light--mocked.png) | Completed-card medians, throughput per UTC day, resource waits and stage table. | `frontend/e2e/project/project-cycle-time-panel.spec.ts`; route mock; panel crop; Last 7 days. | 1072 × 1030 |
| [token-analysis-recorded-spend-dark--pinned-fixture.png](../assets/token-analysis-recorded-spend-dark--pinned-fixture.png) | Usage-kind cost rows, per-card usage, daily model token bars and provider quota line. | `frontend/e2e/workspace/token-analysis.spec.ts`; `token-analysis.fixtures.ts`; four-panel crop; 1280 px source viewport. | 722 × 905 |
| [token-analysis-recorded-spend-light--pinned-fixture.png](../assets/token-analysis-recorded-spend-light--pinned-fixture.png) | Usage-kind cost rows, per-card usage, daily model token bars and provider quota line. | `frontend/e2e/workspace/token-analysis.spec.ts`; `token-analysis.fixtures.ts`; four-panel crop; 1280 px source viewport. | 722 × 905 |

Machine-readable inventory: [captures.json](captures.json). SHA-256 checksums:

- `throughput-cycle-time-history-only-dark--mocked.png`: `98730c0b4944aacc8ad533b5d96405b5acff53efd612c214f09f4c5075786ceb`
- `throughput-cycle-time-history-only-light--mocked.png`: `69efd4411f481739e75282bc7b3d511c849f02c9eec21975e4c865e1824c7d04`
- `token-analysis-recorded-spend-dark--pinned-fixture.png`: `93e2bae198afa2b01f7742ca7dc8864a5616d53fa11198db38558168eae78aad`
- `token-analysis-recorded-spend-light--pinned-fixture.png`: `52b2260c05c793006d8a9ca7ffa05914166f150602f7b4d361373a889f1d67c0`


The figures establish only those captured states. Dark and light throughput
captures use different date windows, as their captions state; they are not a
matched-data theme comparison. Token-analysis captures use the same fixture
in both themes. No production measurements are inferred from fixture numbers.
The diagrams on the world and charts pages are explicitly labelled proposals.
The raw-token timeline's illustrative 71.3 M total is an arithmetic acceptance
example, not a measured or calibrated wave result.
