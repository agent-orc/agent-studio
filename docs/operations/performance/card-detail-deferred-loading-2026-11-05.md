# Card detail deferred loading, 5 November 2026

## Method

The comparison uses the pinned ADR-0056 demo workspace and a generated 830-card
board. The baseline frontend is commit `433f34c6b84e9d50e1901466eacc9dedcf511274`,
which precedes the deferred-loading delivery. The after frontend is this task's
working tree. Both use the same seeded backend and the same Linux host. Chromium
runs headless at 1440 by 900, with service workers blocked and a new browser
context for each first open. There is no CPU or network throttling.

A single browser worker runs three baseline and three after opens in this order:
before, after, before, after, before, after. Each run starts from a fresh board,
clicks the pinned DEMO-9 card, and records the first task detail body (`task-core` or `detail-panes`)
and the second animation frame after `detail-panes` first enters the DOM. Times
start at the card click event. The API count and encoded transfer bytes include
responses received in the first 1.5 seconds after the click. Chrome DevTools Protocol
supplies status and encoded byte counts. The one-minute host load is sampled at
the start of each run. The probe and all six per-request lists are attached in
the task results as `detail-latency-probe.cjs`, `detail-latency-before-requests.json`,
`detail-latency-after-requests.json`, and `detail-latency-runs.json`.
The baseline and after frontends used one backend binary compiled from source at
`69a5348f0`. The backend source is identical in this working tree. The binary
was copied into this worktree from another build on the same host after a
contended local solution build was interrupted. The original AGT-3515 request
list is also attached as
`research-before-requests.json` for comparison with the initial investigation.

## Measurements

| Metric | Before runs | Before median | After runs | After median | Target |
|---|---:|---:|---:|---:|---:|
| Click to first task detail body | 509, 607, 653 ms | 607 ms | 427, 343, 380 ms | 380 ms | Under 400 ms |
| Click to first full pane paint | 1217, 711, 753 ms | 753 ms | 630, 418, 412 ms | 418 ms | Under 700 ms |
| API responses in the first 1.5 s | 36, 38, 37 | 37 | 26, 26, 26 | 26 | At most 20 |
| API transfer in the first 1.5 s | 69.2, 84.1, 82.9 KiB | 82.9 KiB | 55.2, 55.2, 55.2 KiB | 55.2 KiB | At most 60 KiB |

Host one-minute load during the six opens ranged from 5.22 to 6.21 on 12
logical CPUs. No CPU or network throttle was applied.

## Interpretation

The after frontend saved 11 API responses and 27.7 KiB at the median. First
full pane paint improved by 335 ms, and first detail body improved by 227 ms.
The core, paint, and transfer targets were met. The response-count target was
missed by six. The remaining requests include visible Result and overview data
plus shell reads. The request lists show exactly which resources remained in
the whole window.
