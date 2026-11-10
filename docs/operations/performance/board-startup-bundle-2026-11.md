# Board startup bundle, November 2026

AGT-3535 keeps the board shell, columns and cards eager. Task detail,
workspace and admin views, the orchestrator sheet, chat renderers, editors,
CLI views, and large libraries stay outside the initial graph. Task detail
preloads after board paint during browser idle time. The
production `initial` budget is 2,110 kB warning and 2,150 kB error, reduced
from the earlier 3,000 kB warning and 3,800 kB error. The 10 November
candidate build reported 2.09 MB initial raw code and styles, below both limits. The
pre-change build at `433f34c` reported 3.72 MB and a 717.77 kB warning.

## Per-chunk inventory and Chrome traces

The [before inventory](board-startup-bundles-before.json) comes from a
production build of `433f34c6b`; the [after inventory](board-startup-bundles-after.json)
comes from this candidate based on current `develop`. Both used the same
[gzip server](../../../frontend/e2e/perf/gzip-static-server.mjs), a fresh
Chromium context with service workers blocked, and the same mocked 20-card
board. Each JSON lists every JS and CSS chunk with raw bytes from the build
and transferred bytes from Chrome CDP
`Network.loadingFinished.encodedDataLength`, including HTTP overhead. A
request belongs to the phase of its CDP `Network.requestWillBeSent.wallTime`,
compared with `performance.timeOrigin + __boardFirstPaint` and the first
card click timestamp. This timestamp boundary avoids counting requests that
start after paint as cold when Playwright reads the marker later.

| Phase | Before files | Before raw | Before transferred | After files | After raw | After transferred |
|---|---:|---:|---:|---:|---:|---:|
| Cold board | 103 | 3,718,071 B | 1,008,382 B (984.7 KiB) | 107 | 2,086,109 B | 611,201 B (596.9 KiB) |
| Idle prefetch | 1 | 2,483 B | 1,338 B | 30 | 1,194,159 B | 327,016 B (319.4 KiB) |
| First detail open | 10 | 1,006,700 B | 253,503 B (247.6 KiB) | 1 | 21,502 B | 6,204 B (6.1 KiB) |

The cold board is below the 650 KiB gzip JS and CSS target. The task-detail
module begins loading after the first card paints and the browser becomes
idle; the first detail click fetches 247,299 fewer bytes than the baseline
in this matched fixture. A small baseline chunk also started after the paint
marker. The Playwright route-switch check found no duplicate JS or CSS
requests on reopening detail, settings, or chat. Angular's initial build
total and Chrome's cold-board request total use different boundaries.

The task result artifacts contain full phased CDP event streams for
`chrome-network-{before,after}-20-{cold,idle,detail}.json` and the marker
timestamps, plus 830-card traces and screenshots. The
[inventory script](../../../frontend/e2e/perf/network-inventory.py) regenerates
the tables from those traces and the corresponding production builds.
AGT-3515's research capture measured 3,714,321 raw and 1,008,131
transferred cold-board bytes across 104 assets, close to the rebuild above.

## First paint and first detail, earlier cohorts

The 9 November inventory, before the idle preload and timestamp phase
boundary, is retained here as history. It classified requests by the time
Playwright read the first-paint marker, so post-paint requests could appear
in the cold phase under host load.

| Earlier phase | Before files | Before raw | Before transferred | After files | After raw | After transferred |
|---|---:|---:|---:|---:|---:|---:|
| Cold board | 104 | 3,720,554 B | 1,011,262 B | 108 | 2,052,418 B | 603,772 B |
| First detail open | 10 | 1,006,700 B | 253,648 B | 28 | 1,207,928 B | 329,578 B |

`frontend/e2e/perf/board-bundle-lazy.spec.ts` alternates the pre-change and
candidate builds on the same host, using three fresh contexts each for 20
and 830 cards. First paint is the first card insertion plus two animation
frames. Detail open is the first `studio-task` container paint after a card
click. This mocked detail marker does not certify that the bounded core
sections have content.

| Loaded-host cohort | 20-card before paint | 20-card after paint | Improvement | 20-card before detail | 20-card after detail | 830-card before paint | 830-card after paint |
|---|---:|---:|---:|---:|---:|---:|---:|
| First interleaved, three-run medians | 1,614.7 ms | 1,239.3 ms | 375.4 ms | 201.1 ms | 258.8 ms | 3,981.3 ms | 1,040.0 ms |
| Second interleaved, three-run medians | 1,332.0 ms | 1,139.2 ms | 192.8 ms | 158.4 ms | 220.3 ms | 8,463.8 ms | 1,302.4 ms |

The three 20-card first-paint samples were 2,232.9, 1,478.0 and 1,614.7
ms before and 1,372.5, 1,111.3 and 1,239.3 ms after in the first cohort.
They were 1,332.0, 1,171.6 and 1,414.8 ms before and 1,139.2, 1,009.5
and 1,530.2 ms after in the second cohort. The exact paired samples for
both board sizes and detail opens are in the task results.

The first cohort meets the 300 ms target; the second does not. Both show a
roughly 60 ms slower mocked first detail container on the 20-card board
and a faster 830-card board. The second cohort includes a 702.4 ms detail
outlier after the change. These loaded-host results do not establish a
stable 300 ms win or a strict first-detail no-regression result. A separate
live seeded 830-card measurement of full detail pane paint found a before
median of 709.9 ms and after median of 535.8 ms; its method and six samples
are in [card detail deferred loading](card-detail-deferred-loading-2026-11-05.md).

The host has 12 logical CPUs. The 1-minute load average ranged from about
25 to 39 around those interleaved runs and reached 37 during the first gzip
capture; other builds and test processes were active. The first gzip
Playwright attempt timed out waiting for a card, then its retry completed.
The research baseline did not record host load. An experiment that showed
only an empty detail container sooner was reverted.

## Fifteen-pair loaded-host follow-up, 9 November (history)

The before build is the production build of `433f34c6b`, the commit before
the lazy-loading change. The after build is current `develop` at
`dbe406b0b`. Both ran on this host from separate local gzip static servers,
using the same mocked 20-card demo board. One Playwright worker ran the
revisions in 15 interleaved pairs, alternating which went first. Every
sample used a fresh browser context with service workers blocked. No build,
test, or synthetic CPU load was started by this task while the probe ran.
Other fleet processes were active; the 1-minute host load average sampled
at the start of each pair ranged from 33.45 to 47.00 on 12 logical CPUs.

The first-paint marker is the first card insertion plus two animation
frames. The first-detail marker is the first `studio-task` container paint
after a card click. Positive gains below mean before minus after in
milliseconds. The complete machine-readable samples and probe log are
`browser-timing-paired-15.json` and `paired-probe.log` in the task results.

| Pair | Order | 1-min load | Before paint | After paint | Paint gain | Before detail | After detail | Detail gain |
|---:|:---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | B→A | 47.00 | 1547.0 | 1164.7 | +382.3 | 155.5 | 179.6 | -24.1 |
| 2 | A→B | 44.36 | 1009.9 | 900.2 | +109.7 | 107.8 | 166.4 | -58.6 |
| 3 | B→A | 44.36 | 1083.0 | 1337.3 | -254.3 | 156.3 | 211.0 | -54.7 |
| 4 | A→B | 41.93 | 1468.5 | 1108.5 | +360.0 | 178.3 | 269.3 | -91.0 |
| 5 | B→A | 39.77 | 1740.7 | 1651.6 | +89.1 | 144.6 | 497.8 | -353.2 |
| 6 | A→B | 38.51 | 1555.3 | 1126.5 | +428.8 | 137.0 | 245.7 | -108.7 |
| 7 | B→A | 35.74 | 1449.3 | 2186.2 | -736.9 | 149.9 | 555.6 | -405.7 |
| 8 | A→B | 35.77 | 3162.1 | 3724.5 | -562.4 | 234.5 | 1302.8 | -1068.3 |
| 9 | B→A | 42.03 | 1697.9 | 1666.1 | +31.8 | 235.1 | 450.5 | -215.4 |
| 10 | A→B | 40.27 | 2176.2 | 1423.9 | +752.3 | 335.9 | 304.9 | +31.0 |
| 11 | B→A | 36.68 | 2231.9 | 2037.2 | +194.7 | 167.2 | 257.5 | -90.3 |
| 12 | A→B | 35.27 | 1607.6 | 1308.8 | +298.8 | 136.4 | 424.6 | -288.2 |
| 13 | B→A | 35.27 | 2096.9 | 1201.5 | +895.4 | 145.6 | 208.2 | -62.6 |
| 14 | A→B | 33.45 | 1364.1 | 1084.2 | +279.9 | 178.4 | 454.0 | -275.6 |
| 15 | B→A | 33.45 | 1191.9 | 1139.2 | +52.7 | 243.7 | 257.8 | -14.1 |

| Metric | Before 15-run median | After 15-run median | Paired median gain | 95% bootstrap interval for paired gain | First three-run medians, before → after |
|---|---:|---:|---:|---:|---:|
| First paint | 1555.3 ms | 1308.8 ms | +194.7 ms | +31.8 to +382.3 ms | 1083.0 → 1164.7 ms |
| First detail container | 156.3 ms | 269.3 ms | -91.0 ms | -275.6 to -54.7 ms | 155.5 → 179.6 ms |

The interval is the percentile interval from 100,000 bootstrap resamples
of the 15 paired differences, with replacement and a fixed Python random
seed of 3539 for paint and 3542 for detail. The bounds are the 2.5th and
97.5th percentiles. Pairing removes some host-load drift, although the
large load swings within pairs remain visible in the table.

The separate focused spec rerun after this 15-pair probe passed all three
tests. Its three-run 830-card medians were 5,119.2 ms before and 1,035.7 ms
after for first paint, and 447.9 ms before and 216.7 ms after for the first
detail container. Its route-switch assertion found no duplicate JS or CSS
fetches.

**The measured first-paint improvement is 194.7 ms (95% interval 31.8 to
382.3 ms), below the requested 300 ms.** The interval includes 300 ms,
so this sample also cannot establish that the true paired median clears the
target. The mocked first-detail container regressed by a paired median of
91.0 ms (95% interval 54.7 to 275.6 ms slower). The earlier live seeded
830-card full-detail measurement above improved, and the container marker
does not measure populated core sections. This round changes measurement
coverage and documentation only; it did not move detail content or alter
the production bundle based on the container marker.

## Main-thread profile and idle prefetch, 10 November

One fresh Chromium context recorded a CDP CPU profile from navigation until
the first card insertion on the final candidate. The five largest sampled
categories other than idle time were:

| Sampled category | Time before first card insertion |
|---|---:|
| Browser program work | 885.9 ms |
| Anonymous function in `chunk-NAK7S36D.js:2` | 151.2 ms |
| Garbage collection | 82.3 ms |
| Anonymous function in `chunk-Y55HLDUH.js:4` | 65.0 ms |
| DOM `appendChild` | 54.4 ms |

The profile also recorded 1,550.8 ms of idle samples, which are excluded
from the cost list. The first profile before the two small fixes showed
85.3 ms in the closed More menu's `menuItems()` layout read. The next
profile showed 87.8 ms in the active-tab visibility layout check with one
tab. The final profile no longer has either in its five largest categories.
The remaining largest categories are generic browser, rendering and
collection work; the profile does not identify another view that can be
removed while keeping the board and its cards eager. The three CPU profiles
and their top-five summaries are in the task results.

The task-detail component now begins a dynamic import after the first card
has painted and `requestIdleCallback` runs, with a one-second timeout for a
busy browser. The first use shares the browser's module cache, so route
switches do not download a chunk twice. The timestamped network inventory
above separates this idle fetch from the cold board.

## Fifteen-pair interleaved follow-up, 10 November

The before build is `433f34c6b`; the after build is this candidate based on
current `develop`. Both were served by the same local gzip server on the
same host. The [probe](../../../frontend/e2e/perf/board-bundle-lazy.spec.ts)
used the same mocked 20-card board, a fresh Chromium context for every
sample, service workers blocked, one worker, and 15 before/after pairs with
the order alternating by pair. It recorded the one-minute host load average
at the start of each pair. The host has 12 logical CPUs; load ranged from
36.01 to 45.24. No build, test, or synthetic CPU load was started by this
task during the probe. The first-paint marker is the first card insertion
plus two animation frames. The first-detail marker is the first
`studio-task` container paint after the card click. Positive differences
mean the candidate is faster. [All samples](board-startup-paired-2026-11-10.json)
are committed as probe output.

| Pair | Order | 1-min load | Before paint | After paint | Paint gain | Before detail | After detail | Detail gain |
|---:|:---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | B→A | 45.24 | 1677.1 | 1700.2 | -23.1 | 171.9 | 58.0 | +113.9 |
| 2 | A→B | 43.70 | 1426.4 | 1359.6 | +66.8 | 160.4 | 117.6 | +42.8 |
| 3 | B→A | 43.48 | 1554.6 | 1040.0 | +514.6 | 146.0 | 67.6 | +78.4 |
| 4 | A→B | 43.48 | 1134.3 | 1141.1 | -6.8 | 141.7 | 138.6 | +3.1 |
| 5 | B→A | 42.56 | 1218.4 | 1148.0 | +70.4 | 170.5 | 171.4 | -0.9 |
| 6 | A→B | 41.79 | 1316.2 | 1096.1 | +220.1 | 153.7 | 65.4 | +88.3 |
| 7 | B→A | 41.79 | 1771.9 | 1243.7 | +528.2 | 161.1 | 194.8 | -33.7 |
| 8 | A→B | 41.81 | 1405.0 | 1849.3 | -444.3 | 138.9 | 83.8 | +55.1 |
| 9 | B→A | 42.47 | 1348.9 | 1180.7 | +168.2 | 132.3 | 170.7 | -38.4 |
| 10 | A→B | 39.95 | 1216.6 | 1199.2 | +17.4 | 215.4 | 156.3 | +59.1 |
| 11 | B→A | 38.03 | 1213.8 | 1237.7 | -23.9 | 124.9 | 57.5 | +67.4 |
| 12 | A→B | 38.03 | 1234.5 | 1115.9 | +118.6 | 123.7 | 66.9 | +56.8 |
| 13 | B→A | 37.15 | 1598.9 | 1387.1 | +211.8 | 231.7 | 116.7 | +115.0 |
| 14 | A→B | 36.01 | 1212.8 | 1102.7 | +110.1 | 144.6 | 119.2 | +25.4 |
| 15 | B→A | 36.01 | 1621.0 | 1196.3 | +424.7 | 131.4 | 80.2 | +51.2 |

| Metric | Before 15-run median | After 15-run median | Paired median gain | 95% bootstrap interval for paired gain | First three-run medians, before → after |
|---|---:|---:|---:|---:|---:|
| First paint | 1348.9 ms | 1196.3 ms | +110.1 ms | +17.4 to +220.1 ms | 1554.6 → 1359.6 ms |
| First detail container | 146.0 ms | 116.7 ms | +55.1 ms | +25.4 to +78.4 ms | 160.4 → 67.6 ms |

The 95% percentile intervals use 100,000 bootstrap resamples of the 15
paired differences, with replacement, and fixed Python seeds 3539 for
paint and 3542 for detail. Regenerate both intervals from the committed
samples with one command:

```sh
python3 frontend/e2e/perf/paired-bootstrap.py docs/operations/performance/board-startup-paired-2026-11-10.json
```

**The paired first-paint gain is 110.1 ms (95% interval 17.4 to 220.1 ms),
below the requested 300 ms.** The interval excludes 300 ms. The two cheap
startup layout checks identified by the CPU profiles were removed, but the
remaining profile is dominated by generic browser, DOM and collection work.
Reaching 300 ms on this loaded host would require reducing eager board and
shell rendering work, then validating the change on the 830-card board.

**The paired first-detail gain is 55.1 ms (95% interval 25.4 to 78.4 ms).**
The significant regression seen in the 9 November probe is resolved for
the same first-container marker. In the separate three-run 830-card
captures, the first-paint medians were 7262.1 ms before and 1672.8 ms
after; the first-detail medians were 666.3 ms before and 95.6 ms after.
These 830-card samples were taken sequentially rather than paired,
so they support a no-regression check but do not estimate a stable gain.
