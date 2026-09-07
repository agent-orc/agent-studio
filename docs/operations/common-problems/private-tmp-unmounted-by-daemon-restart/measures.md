# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| applied | 2026-09-07 | Interim operator drop-in `20-no-private-tmp.conf` with `PrivateTmp=false` on both units of agent-runner-01, followed by `daemon-reload` and a review-unit restart. | operator | Stopped the bleeding on one host. Not durable: the deploy scripts and unit templates still shipped `PrivateTmp=true`. |
| applied | 2026-09-07 | `PrivateTmp=false` in all three shipped unit generators and in the versioned `10-agent-runner-hardening.conf` drop-in; `harden-agent-runner-host.sh` verifies the effective value and removes the interim drop-in. | AGT-2750 | The interim drop-in can be removed from agent-runner-01. Regression test: `RunnerServiceUnitTests.Runner_units_never_bind_a_private_tmp_namespace_to_the_daemon_lifecycle`. |
| applied | 2026-09-07 | Startup re-adoption reads `/proc/<pid>/mountinfo` and refuses a worker whose `/tmp` mount root ends in `//deleted`. | AGT-2750 | A worker stranded by a pre-fix host settles as infrastructure at once instead of after an hour of failing builds. Regression test: `DetachedWorkerTempNamespaceTests`. |
| applied | 2026-09-07 | `MSB1025`, `SocketException (99)` on a pipe, and `mkdtemp ... ENOENT`, combined with no parsed test result, classify as `ReviewInfra / HostTempUnavailable` in the Review Executor and at the Task Server. | AGT-2750 | Such a run retries instead of blocking the card. Regression tests: `RemoteReviewAuthorityTests.A_command_that_lost_the_host_temp_namespace_is_infrastructure` (fails without the fix) and the workspace fixture theory. |
