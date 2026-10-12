# Integration lane measurement, AGT-3070

The Task Server appends typed `integration-lane-events.jsonl` records to each delivery folder. The 24-hour lane projection and the 7-day or 30-day gate projection share `GET /api/gate-economics`. The endpoint reads per-delivery gate receipt headers and durable batch execution facts for gate time, wait and rung; it reads typed lane events for arrival, lock, gate and publication times. Per-delivery queue wait is machine gate wait plus merge-lock wait. One batch gate `durationMs` is divided by its manifest member count to express minutes per merge. A missing historical field is unknown.

| Model input | Measured definition | First post-instrumentation week |
|---|---|---|
| g | Median and p90 `durationMs` of the batch-flow gate receipts | Pending 12 to 19 October 2026 |
| f | Red gate-finished events divided by terminal gate-finished events | Pending a complete delivery wave |
| p | Delivery dequeue to gate request, less merge-lock wait | Pending a complete delivery wave |
| lambda | Enqueued deliveries per hour by project | Pending a complete delivery wave |

The historical 28 to 29 September Windows receipts are a comparison, not batch-flow input. All eight were `Fail` with `failureKind=Code` and `budget=none`. Seven stopped in `backend.Tests` with three omitted test commands; one stopped in `runner.Tests` with two omitted commands. The median historical gate duration was about 51 minutes. The complete receipt table, exact milliseconds and audit counts are in the AGT-3070 delivery report.

As of 12 October, the live Agent Studio batch-gate report had 0 closed batches, 0 eligible members and 0 full-suite runs; its green rate and latency were null. No full week of events from this instrumentation exists. Whether an agent round coincided with a stalled project queue, and what held that queue, are therefore unmeasured. AGT-W70 slices 2 to 5 and AGT-W69 slices 2 to 4 remain unpromoted until one full wave and the first complete week are observed. This is the working no-go for additional lane or gate behavior changes.
