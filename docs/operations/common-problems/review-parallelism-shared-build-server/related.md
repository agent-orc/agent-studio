# Related

- [[services-killed-by-harness-sweep]] - the mirror image: a sweep that reaps too
  much. Read both before widening any process-matching rule.
- AGT-2750 (`TmpMountTornDown`) - the adjacent incident. There a `PrivateTmp`
  unit restart deleted the worker's `/tmp` mount; here the mount is intact and
  the server that outlives its attempt is the shared resource. Both end as
  infrastructure classifications rather than product regressions.
- AGT-2759 (deleted-cwd zombies) - why `CleanupAsync` reaps every process rooted
  in the attempt directory before deleting it.
- Code: [`runner/ReviewBuildServerIsolation.cs`](../../../../runner/ReviewBuildServerIsolation.cs),
  [`runner/CommandProgressWatchdog.cs`](../../../../runner/CommandProgressWatchdog.cs),
  [`runner/RemoteReviewWorkspace.cs`](../../../../runner/RemoteReviewWorkspace.cs).
- Probe: [`scripts/review-build-server-isolation-probe.sh`](../../../../scripts/review-build-server-isolation-probe.sh).
- Docs: [linux-runner-host.md](../../setup/linux-runner-host.md) review parallelism
  section, [review.md](../../../system/domains/review.md).
