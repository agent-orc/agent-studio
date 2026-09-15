# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| tried | 2026-09-15 | Reduce the review role to `RUNNER_MAX_PARALLELISM=2` | operator | Stopped the deadlocks and created a 20+ card review backlog. Superseded. |
| works | 2026-09-15 | `ReviewBuildServerIsolation` applied to the candidate, baseline and preparation environments | AGT-2831 | Four concurrent attempts of one subject each report `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`, `UseSharedCompilation=false` and a private `MSBUILDDEBUGPATH`. Isolated probe mode leaves zero reusable nodes and zero attempt-owned compiler servers. |
| works | 2026-09-15 | `CommandProgressWatchdog` reaps a verify command whose process tree stops consuming CPU and classifies it `ReviewInfra/NoCpuProgress` | AGT-2831 | Four attempts blocked on one host-shared handle end on the watchdog window instead of the command budget. Without the watchdog the same test was still running after 90 s against a 3600 s budget. |
| works | 2026-09-15 | `scripts/review-build-server-isolation-probe.sh` as an operator probe | AGT-2831 | Reports rendezvous-point location, adoptable node pool and per-mode leakage; writes to `$JOB_RESULTS_DIR`. |
