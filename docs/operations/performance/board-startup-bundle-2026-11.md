# Board startup bundle, November 2026

AGT-3535 keeps the board shell, columns and cards eager. Task detail,
workspace and admin views, the orchestrator sheet, chat renderers, editors,
CLI views, and large libraries load on their first route or use. The
production `initial` budget is 2,050 kB warning and 2,100 kB error, reduced
from the earlier 3,000 kB warning and 3,800 kB error. The 9 November build
reported 2.02 MB initial raw code and styles, below both limits. The
pre-change build at `433f34c` reported 3.72 MB and a 717.77 kB warning.

## Per-chunk inventory and Chrome traces

The [before inventory](board-startup-bundles-before.json) comes from a
production build of `433f34c`; the [after inventory](board-startup-bundles-after.json)
comes from the final production build. Both were served by the same gzip
static server to a fresh Chromium context with service workers blocked and
the same mocked 20-card board. Each JSON lists every JS and CSS chunk with
raw bytes from the build and transferred bytes from Chrome CDP
`Network.loadingFinished.encodedDataLength`, including HTTP overhead. A
request belongs to the phase in which Chrome initiated it, even if it
finished in the next phase.

| Phase | Before files | Before raw | Before transferred | After files | After raw | After transferred |
|---|---:|---:|---:|---:|---:|---:|
| Cold board | 104 | 3,720,554 B | 1,009,750 B (986.1 KiB) | 107 | 2,027,381 B | 593,609 B (579.7 KiB) |
| First detail open | 10 | 1,006,700 B | 253,515 B (247.6 KiB) | 29 | 1,199,834 B | 328,001 B (320.3 KiB) |

The cold board is below the 650 KiB gzip JS and CSS target. The detail
open fetches 74,486 more bytes in this matched fixture because code moved
out of the initial graph. The Playwright route-switch check found no
duplicate JS or CSS requests on reopening detail, settings, or chat.
Angular's initial build total and Chrome's cold-board request total use
different boundaries; the 2.02 MB build total and 2,027,381 B request
total happened to be close for this revision.

The task result artifacts contain full CDP event streams for the matched
`chrome-network-before-20-cold.json`, `chrome-network-before-20-detail.json`,
`chrome-network-after-gzip-20-cold.json`, and
`chrome-network-after-gzip-20-detail.json` captures. They also contain
the gzip server script and six timing samples for each revision. AGT-3515's
research capture is retained separately as `chrome-network-before-22-*.json`;
it measured 3,714,321 raw and 1,008,131 transferred cold-board bytes
across 104 assets, close to the rebuild above.

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
25 to 39 around the interleaved runs and reached 37 during the first gzip
capture; other builds and test processes were active. The first gzip
Playwright attempt timed out waiting for a card, then its retry completed.
The research baseline did not record host load. Repeat the same interleaved
probe on an idle host before using the 300 ms improvement as a release
performance claim. No product code is changed in this follow-up; an
experiment that showed only an empty detail container sooner was reverted.
