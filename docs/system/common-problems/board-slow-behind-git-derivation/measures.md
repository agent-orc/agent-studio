# Measures

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| done | 2026-09-01 | AGT-2699 to AGT-2702: remove no-op invalidations and per-scan costs. | Platform | Reduced repeated work; the remaining cost stayed structural. |
| done | 2026-09-07 | AGT-2726: one background git index per repository; request paths read its last capture and carry a freshness stamp. | Platform | Replayed request mix produced 19 git spawns against about 3,980 before; `tasks/grouped` p95 under 1 ms. |
| open | 2026-09-07 | AGT-2703: ETag and payload trim on the board endpoints. | Platform | Deliberately out of scope for AGT-2726; the index had to exist first. |
