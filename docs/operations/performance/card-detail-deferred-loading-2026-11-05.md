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
