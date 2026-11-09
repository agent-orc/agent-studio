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
