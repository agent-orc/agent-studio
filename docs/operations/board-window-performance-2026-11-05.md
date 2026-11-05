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

The host load was about 26 to 70 during the delivered AGT-3534 measurements and roughly 32 to 85 during this follow-up. The 20-card cold baseline moved from 2.45 s in AGT-3515 to 7.16 s in an earlier AGT-3534 benchmark and 8.10 s here on the loaded host. Absolute paint targets of 1.5 s at 830 and 5 s at 3,000 were **not met in these runs**. The 3,000-card DOM and heap targets (under 100,000 nodes and 80 MiB) and the under-2-second longest-task target were met. The corrected build's three-run 830-card menu-move median was 84.8 ms, within the 100 ms target; an earlier delivered run recorded 105.4 ms. These timings are sensitive to shared-host contention. A same-process, one-worker interleaved A/B run is still needed for a fair paint-time comparison.
