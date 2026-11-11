# Board startup bundle, November 2026

AGT-3535 keeps the board shell, columns and cards eager. Task detail,
workspace and admin views, the orchestrator sheet, chat renderers, editors,
CLI views, and large libraries load on their first route or use. The
production `initial` budget is 2,050 kB warning and 2,100 kB error, reduced
from the earlier 3,000 kB warning and 3,800 kB error. The current 9 November
build reported 2.05 MB initial raw code and styles, below both limits. The
pre-change build at `433f34c` reported 3.72 MB and a 717.77 kB warning.

The 11 November follow-up below (AGT-3745) moved the initial total to
1,922,039 B and replaced this ratchet with the 3 % warning and 10 % error
rule in [the frontend domain doc](../../system/domains/frontend.md#initial-bundle-budget).

## Per-chunk inventory and Chrome traces

The [before inventory](board-startup-bundles-before.json) comes from a
production build of `433f34c`; the [after inventory](board-startup-bundles-after.json)
comes from current `develop` at `dbe406b0b`. Both were served by the same gzip
static server to a fresh Chromium context with service workers blocked and
the same mocked 20-card board. Each JSON lists every JS and CSS chunk with
raw bytes from the build and transferred bytes from Chrome CDP
`Network.loadingFinished.encodedDataLength`, including HTTP overhead. A
request belongs to the phase in which Chrome initiated it, even if it
finished in the next phase.

| Phase | Before files | Before raw | Before transferred | After files | After raw | After transferred |
|---|---:|---:|---:|---:|---:|---:|
| Cold board | 104 | 3,720,554 B | 1,011,262 B (987.6 KiB) | 108 | 2,052,418 B | 603,772 B (589.6 KiB) |
| First detail open | 10 | 1,006,700 B | 253,648 B (247.7 KiB) | 28 | 1,207,928 B | 329,578 B (321.9 KiB) |

The cold board is below the 650 KiB gzip JS and CSS target. The detail
open fetches 75,930 more bytes in this matched fixture because code moved
out of the initial graph. The Playwright route-switch check found no
duplicate JS or CSS requests on reopening detail, settings, or chat.
Angular's initial build total and Chrome's cold-board request total use
different boundaries; the 2.05 MB build total and 2,052,418 B request
total happened to be close for this revision.

The task result artifacts contain full CDP event streams for the matched
`chrome-network-before-20-cold.json`, `chrome-network-before-20-detail.json`,
`chrome-network-after-develop-20-cold.json`, and
`chrome-network-after-develop-20-detail.json` captures. They also contain
the gzip server script and six timing samples for each revision. AGT-3515's
research capture measured 3,714,321 raw and 1,008,131 transferred cold-board
bytes across 104 assets, close to the rebuild above.

## First paint and first detail

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

## Fifteen-pair loaded-host follow-up, 9 November

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
coverage and documentation only; it does not move detail content or alter
the production bundle based on the container marker.

## Start bundle follow-up, 11 November (AGT-3745)

Batch gates failed because several deliveries, each within budget on its own
base, crossed the 2,102 kB `maximumError` together by a few hundred bytes.
`develop` at `3c3f72609` measured 2,100,405 B initial in 107 files, 1,595 B
below the limit. Raw sizes from `npm --prefix frontend run build --
--stats-json`; Angular counts 1 kB as 1,000 B.

| Largest eager chunk (before) | Raw size | Main modules |
|---|---:|---|
| `main` | 426,924 B | `app.ts` 105,440, `studio-shell.component.ts` 86,260, explorer tree 27,981, crash-recovery prompt 17,553, status bar 16,734 |
| board chunk | 417,721 B | task card 72,768, task column 46,890, card view model 28,546, delivery section 17,609, live status 16,508 |
| Angular core | 188,047 B | `@angular/core` |
| `styles.css` | 114,548 B | `styles.scss` 97,445, `diff2html.min.css` 17,085 |
| task service | 105,359 B | `task.service.ts` 41,303, `@microsoft/signalr` |
| board state | 80,397 B | task selection, board filters, tab state |
| `@angular/cdk` | 62,506 B | drag and drop for the explorer tree |
| Task-Server panels | 42,012 B | three panels pulled in by `isLocalUrl` through the feature barrel |
| `@angular/forms` | 39,321 B | `FormsModule`, used only by the sign-in gate |

| Move | Bytes out of the start bundle |
|---|---:|
| `FormsModule` dropped from `app.ts` and the studio shell (unused there); the sign-in gate deferred | 52,075 |
| `isLocalUrl` and `TaskServerService` imported from the new `task-server/runtime.ts`, not the component barrel | 34,981 |
| Update Center deferred until it opens; Updates settings moved to `update/lazy.ts` | 18,819 |
| Crash-recovery prompt deferred in the status bar | 17,553 |
| diff2html stylesheet linked by `loadDiff2Html()` instead of the global bundle | 17,085 |
| Quota forecast panel, curve and workspace disclosure moved to `quota/lazy.ts` | 18,723 |
| Legacy-layout header chrome (filters dropdown, project tabs, auto-review indicator, version badge, usage host) deferred with that layout | 19,547 |

After the change the initial total is 1,922,039 B in 110 files, 178,366 B
(8.5 %) less. The rows sum to 178,783 B; the defer wiring adds 661 B to
`app.ts` and 113 B to the status bar, and chunk wrappers account for the rest. The budget is now 1,980 kB warning and 2,114 kB error. The board
shell, columns, cards, explorer tree and the always-open inline filter panel
stay eager; the filter panel registers its Escape handler at startup, so
deferring it would change Escape handling.

