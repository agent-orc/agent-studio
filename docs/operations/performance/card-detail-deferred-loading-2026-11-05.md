# Card detail deferred loading, 5 November 2026

## Method

The review comparison uses the opt-in Playwright case
`DEMO-9 first-open request and paint benchmark on an 830-card board` in
`frontend/e2e/task-detail/task-detail-instant-navigation.spec.ts`.
It serves each frontend revision's production bundle on the same host, with
one Chromium worker. A fresh browser page opens a mocked DEMO-9 card from a
board containing that card and 829 synthetic peers. The API fixtures are
identical for both revisions, including a 250 ms document response delay so
the bounded core can paint. A capture-phase click listener marks the start.
A DOM observer marks core ready after all seven bounded core sections exist
and one animation frame; it marks the pane after the Overview title and pane
body exist plus two animation frames. The first 1.5 s API list includes
completed responses, with bytes measured from response bodies. These are
uncompressed fixture bytes, not wire transfer bytes.

Runs alternated after, before, after, before, after, before on the same host.
Each raw JSON report records the host's 1, 5 and 15 minute load averages and
logical CPU count. During these runs, 1-minute load ranged from 4.24 to 7.60
on 12 logical CPUs; 5-minute load ranged from 6.82 to 7.64. The fixture
comparison isolates frontend behavior; it does not replace the live DEMO-9
measurement on the 830-card workspace.

## Research baseline

AGT-3515 measured the live DEMO-9 card on 5 November 2026. One opening
produced 39 API responses and 81.7 KiB of API transfer in 1.5 s. On an
830-card board, click to core ready was 402 ms and full pane paint was 952 ms.
Those values are context, not members of the interleaved fixture cohort.

## Interleaved fixture comparison

| Revision | Run | Core ready (ms) | Pane paint (ms) | API responses | API body (KiB) | Host load (1 min) |
|---|---:|---:|---:|---:|---:|---:|
| Before (`433f34c`) | 1 | 388.1 | 605.3 | 36 | 4.16 | 7.60 |
| After (AGT-3536 working tree) | 1 | 497.5 | 1109.8 | 20 | 3.27 | 4.24 |
| Before | 2 | 268.9 | 667.3 | 38 | 4.16 | 5.24 |
| After | 2 | 118.0 | 434.8 | 19 | 3.27 | 5.35 |
| Before | 3 | 211.4 | 630.0 | 37 | 4.16 | 4.67 |
| After | 3 | 102.0 | 501.8 | 19 | 3.27 | 4.90 |
| **Before median** | | **268.9** | **630.0** | **37** | **4.16** | |
| **After median** | | **118.0** | **501.8** | **19** | **3.27** | |

The fixture medians meet the 400 ms core, 700 ms pane and 20-response targets.
The mocked body sizes cannot establish whether the live 60 KiB wire-transfer
target was met. The first after sample was an outlier; it remains in the
three-run median. The per-run request lists are attached as JSON in the task
result directory as `card-detail-before-{1,2,3}.json` and
`card-detail-after-{1,2,3}.json`.

## Live seeded DEMO-9 API comparison, 8 and 9 November 2026

The follow-up probe reuses AGT-3515's `generate`, `createServer` and browser
initialization functions. It serves the production bundles for baseline
`433f34c` and the current revision on alternate turns, with one Chromium
worker and one isolated demo backend seeded by `seed-demo-workspace.mjs`.
The board contains the seeded visible cards plus synthetic peers to reach
830 cards. The selected card is the actual seeded DEMO-9 record, and every
detail API request passes through to that backend. Each turn uses a fresh
browser context with service workers blocked. As in AGT-3515, the click is
marked by a capture-phase listener, core readiness is the first insertion of
`detail-panes`, and full pane paint is two animation frames later. A Chrome
DevTools Protocol network listener records completed API responses in the
first 1.5 seconds after the click and sums `encodedDataLength`, which includes
the compressed response transfer and headers. The six full request lists and
the exact probe scripts are attached to this task's result directory as
`card-detail-live-{before,after}-{1,2,3}.json`, `detail-live-probe.cjs`,
`measure.cjs` and `run-live-comparison.sh`.

The first complete cohort ran while the 12-CPU host was carrying other builds
and browser tests. Its one-minute load ranged from 8.40 to 10.47. The request
and transfer targets passed after the change, but the 400 ms core target did
not. These samples remain in the task results as
`card-detail-busy-host-{before,after}-{1,2,3}.json` so the load effect is
visible rather than discarded.

| Revision | Run | Core ready (ms) | Pane paint (ms) | API responses | API transfer (KiB) | Host load (1 min) |
|---|---:|---:|---:|---:|---:|---:|
| Before (`433f34c`) | 1 | 842.9 | 954.3 | 41 | 84.7 | 8.57 |
| After (AGT-3536 working tree) | 1 | 478.0 | 661.1 | 20 | 52.5 | 8.52 |
| Before | 2 | 812.9 | 901.9 | 37 | 82.8 | 8.40 |
| After | 2 | 539.6 | 715.6 | 20 | 52.5 | 10.29 |
| Before | 3 | 767.2 | 972.4 | 39 | 84.2 | 10.47 |
| After | 3 | 440.6 | 560.6 | 20 | 52.5 | 9.88 |
| **Before median** | | **812.9** | **954.3** | **39** | **84.2** | |
| **After median** | | **478.0** | **661.1** | **20** | **52.5** | |

The change moves the three pipeline decision receipt reads below the viewport
to the point when that section is scrolled into view. It also reads previous
result history only when its disclosure opens. The original post-change
request list had 24 responses and 53.6 KiB median at a 1-minute host load of
6.45 to 7.08. These four deferred reads bring the complete cohort to 20
responses and 52.5 KiB median. The production bundle was rebuilt after those
edits.

The final cohort used the rebuilt production bundle after lint. All six
openings ran on the same host with one Chromium worker, in before/after pairs.
The 12-CPU host's one-minute load ranged from 1.97 to 4.07. Each sample used
the same seeded backend, browser context settings and 830-card board. The
first pair was slower despite the lowest reported load; the median keeps
that sample rather than dropping it.

| Revision | Run | Core ready (ms) | Pane paint (ms) | API responses | API transfer (KiB) | Host load (1 min) |
|---|---:|---:|---:|---:|---:|---:|
| Before (`433f34c`) | 1 | 846.2 | 956.3 | 41 | 85.9 | 2.06 |
| After (AGT-3536 working tree) | 1 | 699.9 | 893.7 | 20 | 52.5 | 2.06 |
| Before | 2 | 580.9 | 599.8 | 38 | 84.0 | 1.97 |
| After | 2 | 365.1 | 516.0 | 20 | 52.5 | 1.97 |
| Before | 3 | 540.3 | 709.9 | 39 | 84.2 | 4.07 |
| After | 3 | 356.4 | 535.8 | 20 | 52.5 | 4.07 |
| **Before median** | | **580.9** | **709.9** | **39** | **84.2** | |
| **After median** | | **365.1** | **535.8** | **20** | **52.5** | |

The final after median meets all four targets: core below 400 ms, pane below
700 ms, no more than 20 API responses and no more than 60 KiB API transfer.
The complete final request lists are in
`card-detail-live-{before,after}-{1,2,3}.json` in the task results.
