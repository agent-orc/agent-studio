# Board card window measurement, 5 November 2026

AGT-3534 mounts cards only near each lane viewport. This record preserves the measurements that informed delivery and the limits of their comparison. All values are from the shared Linux runner with a production frontend build and headless Chrome for Testing 147. The fixture contains generated cards, never live task data.

## Method and results

AGT-3515 cloned pinned presentation cards into 20 visible cards, 830 cards and 3,000 cards. Its probe timed the first card insertion plus two animation frames, captured the longest `PerformanceObserver` long task, counted DOM nodes, and read `JSHeapUsedSize` after CDP garbage collection. The 20 and 830 values are three-run medians; the 3,000 values are single stress runs. Cold means a new context and warm means a reload with cached static assets.

The AGT-3534 window probe used generated ready-lane records and the same browser APIs, but timed one animation frame after the first card. It used a 1280 × 800 viewport instead of AGT-3515's 1440 × 900. Each after value below is a three-run median from the corrected build's benchmark (`frontend/e2e/perf/board-window-benchmark.spec.ts`, three parallel workers). These fixture and timing differences mean the paint values are **not a controlled before/after speedup**. Node and heap figures show the effect of bounded card mounting, with the same caveat about fixture shape.

| Cards | Cache | Before paint | After paint | Before nodes | After nodes | Before heap | After heap | Before longest task | After longest task |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | Cold | 2.45 s | 8.10 s | 7,951 | 1,678 | 13.8 MiB | 13.55 MiB | 654 ms | 1,535 ms |
| 20 | Warm | 1.59 s | 6.99 s | 7,951 | 1,678 | 13.7 MiB | 13.55 MiB | 500 ms | 1,526 ms |
| 830 | Cold | 4.62 s | 7.70 s | 183,991 | 1,180 | 94.6 MiB | 13.57 MiB | 3,320 ms | 1,500 ms |
| 830 | Warm | 4.49 s | 7.32 s | 183,991 | 1,180 | 94.5 MiB | 13.58 MiB | 3,530 ms | 1,203 ms |
| 3,000 | Cold | 53.18 s | 9.01 s | 655,211 | 1,180 | 306.4 MiB | 16.29 MiB | 39,720 ms | 1,702 ms |
| 3,000 | Warm | 75.01 s | 7.40 s | 655,211 | 1,180 | 306.4 MiB | 16.39 MiB | 59,930 ms | 907 ms |

Source for before values: the AGT-3515 `results/report.html` and `measurements.json` on this runner. Source for after values: the AGT-3534 follow-up benchmark and generated JSON in its result directory. The 20-card before fixture is the presentation demo; the after fixture uses smaller synthetic records. The 3,000-card before result has one run per cache state and is less stable than a median.

The host load was about 26 to 70 during the delivered AGT-3534 measurements and roughly 32 to 85 during this follow-up. The 20-card cold baseline moved from 2.45 s in AGT-3515 to 7.16 s in an earlier AGT-3534 benchmark and 8.10 s here on the loaded host. Absolute paint targets of 1.5 s at 830 and 5 s at 3,000 were **not met in these runs**. The 3,000-card DOM and heap targets (under 100,000 nodes and 80 MiB) and the under-2-second longest-task target were met. The corrected build's three-run 830-card menu-move median was 84.8 ms, within the 100 ms target; an earlier delivered run recorded 105.4 ms. These timings are sensitive to shared-host contention.

## Follow-up measurement, 8 November 2026

The same production frontend build and Chrome for Testing 147 ran the generated-board benchmark with one Playwright worker. A frontend-only server answered the board and unrelated shell API calls with local fixtures, without starting the backend. Each size had three cold navigations in fresh browser contexts and three warm reloads in those contexts. The probe recorded the first card and count after one animation frame, longest browser long task, DOM element count, and JS heap after CDP garbage collection. The test also verified the 41-card footer controls, 830-card search, paging, drag and menu moves, and 3,000-card bounded window with light/dark screenshots at 1280 and 390 pixels. All seven tests passed. Values are three-run medians.

| Cards | Cache | First board paint | Longest task | DOM nodes | Heap after GC |
|---:|---|---:|---:|---:|---:|
| 20 | Cold | 1.165 s | 297 ms | 1,575 | 10.24 MiB |
| 20 | Warm | 1.118 s | 250 ms | 1,575 | 10.25 MiB |
| 830 | Cold | 1.146 s | 220 ms | 1,067 | 9.94 MiB |
| 830 | Warm | 1.140 s | 238 ms | 1,067 | 9.94 MiB |
| 3,000 | Cold | 1.779 s | 312 ms | 1,067 | 11.47 MiB |
| 3,000 | Warm | 1.540 s | 240 ms | 1,067 | 11.53 MiB |

The 830-card first-paint target of 1.5 s, 3,000-card target of 5 s, 100,000-node and 80-MiB limits, and 2-second longest-task limit were met in this run. The three-run 830-card menu-move median was 53.9 ms, below its 100 ms target. The host's one-minute load average rose from about 47 to 50 during the run on 12 cores; this was **not an idle-host run**. These values show target compliance with this generated fixture under that load, but are not an interleaved A/B comparison to AGT-3515: its presentation-card fixture, viewport and two-frame paint probe differ. An interleaved merge-base A/B measurement remains open if a controlled speedup claim is needed.

The same seven specs also passed through the repository's isolated E2E stack with a Debug backend publish. The production frontend and generated board fixture were identical; the fixture still answered the shell API calls, so this is not a live-data or live-API benchmark. The host one-minute load average was about 44 on 12 cores near the end of this run. Three-run medians were:

| Cards | Cache | First board paint | Longest task | DOM nodes | Heap after GC |
|---:|---|---:|---:|---:|---:|
| 20 | Cold | 5.107 s | 1,159 ms | 1,575 | 10.36 MiB |
| 20 | Warm | 3.748 s | 679 ms | 1,575 | 10.36 MiB |
| 830 | Cold | 1.577 s | 311 ms | 1,067 | 10.47 MiB |
| 830 | Warm | 1.896 s | 261 ms | 1,067 | 10.46 MiB |
| 3,000 | Cold | 1.404 s | 263 ms | 1,067 | 13.05 MiB |
| 3,000 | Warm | 1.845 s | 201 ms | 1,067 | 13.12 MiB |

The 830-card 1.5-second paint target was **missed in the isolated-stack run** by 77 ms cold and 396 ms warm; the 3,000-card, DOM, heap, longest-task and lane-move targets were met. The lane-move median was 45.2 ms. The 20-card baseline also took substantially longer than the 830-card board in this run, indicating strong timing noise from the shared host. A reproducible idle-host pass, with a stable 20-card baseline, remains unverified.

## Follow-up measurement, 9 November 2026

The frontend-only mocked server, production build and Chrome for Testing 147 ran the same generated-board fixture with one Playwright worker. The API harness now rejects every unlisted request; its prior blanket `[]` fallback was removed. The complete large-board behavior spec passed with this strict fixture. Each measurement below is a median of three cold navigations in new contexts or three warm reloads, at 1280 × 800. The before and after `uptime` readings for the 830-card run were 30.90 and 37.02 one-minute load average on a 12-core host. Other builds were active, so this is **not the requested nearly idle confirmation**.

| Cards | Cache | First board paint | Longest task | DOM nodes | Heap after GC |
|---:|---|---:|---:|---:|---:|
| 20 | Cold | 1.217 s | 232 ms | 1,575 | 10.24 MiB |
| 20 | Warm | 1.322 s | 298 ms | 1,575 | 10.26 MiB |
| 830 | Cold | 2.044 s | 409 ms | 1,067 | 9.94 MiB |
| 830 | Warm | 1.791 s | 328 ms | 1,067 | 9.96 MiB |
| 3,000 | Cold | 2.385 s | 406 ms | 1,067 | 11.43 MiB |
| 3,000 | Warm | 1.555 s | 343 ms | 1,067 | 11.49 MiB |

The 20 and 3,000-card control run followed immediately; its ending one-minute load average was 37.67. The 830-card raw samples ranged from 1.477 to 2.851 s cold and 1.245 to 1.810 s warm, showing material host noise. The 830-card median missed the 1.5 s target in both cache states; the other listed performance targets passed. A follow-up probe now records board response, script and layout durations to locate the remaining time if the target is still missed on a quieter host.

The complete benchmark spec ran again after adding that profile probe. `uptime` read 45.80 before and 38.32 after, and 73.48 during the run. The 20-card first-paint median rose to 9.056 s cold and 6.691 s warm; 830 cards took 4.844 s cold and 4.615 s warm; 3,000 took 4.390 s cold and 4.359 s warm. The 830-card menu-move median was 111.2 ms. This run missed the paint and move targets, but even the 20-card control was slower than the 3,000-card board, so it does not isolate a board-size regression.

For the 830-card cold samples, the local mock board response ended at 3.308–3.687 s, leaving 0.789–1.537 s until the first card paint. CDP recorded 1.284–1.699 s total script duration and 0.120–0.324 s layout duration by the sample point. The late response on a local server and the much slower 20-card control point to shared CPU contention as the dominant limit of this run. The probe does not attribute all script time to the board, and an idle-host confirmation remains open.
