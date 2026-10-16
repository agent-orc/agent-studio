# AGT-3155 Linux installer review fix

- Result SHA: `765bcc1566c8e575795b763b0805cd4433af8906` (duplicate-script removal, rebased onto `86878facd38f190124627338d2bcc986c3b36852`).
- Removed the unreferenced `scripts/release/install.sh`. Release packaging continues to use the verified root `install.sh`.
- Verification passed: `dotnet build agent-taskboard.sln --no-restore`; `sh scripts/release/linux-install-script.test.sh`; `sh scripts/release/release-scripts.test.sh`; setup tests filtered to `LinuxInstallerTests|InstallationJourneyTests` (86 passed); architecture and guard filter on all eight .NET test projects (82 matching backend tests passed); `FullyQualifiedName~Architecture` in backend (103 passed) and Task Server (5 passed); `npm --prefix frontend run lint`; `git diff --check`.
- No release tag or GitHub Release was created, and package visibility was not changed. Publishing a release and setting package visibility remain operator steps.

---

# AGT-3182 Product contributor role status

- Result SHA (rebased implementation HEAD): `999886ec7097c81175c6a358f54e2fe9536ad8e6`, rebased onto `acb056b20b9dccf0040432f6cf61bfdc7dc8e8f2` from `origin/develop`.
- Follow-up fix SHA-256 (worktree diff for `task-server/StudioContributorMiddleware.cs`): `2f1133c06478d5d01f5cc114f2b73cd89da1f7a40160b1c8c60c106eb5534cdd`. The managed run leaves this final edit uncommitted for platform git ownership.
- Implemented option: owner-issued, single-use expiring invitation link with recipient-chosen password; Product contributor access is scoped to assigned projects, with a separate owner-granted production approval permission.
- Fixed the Task Server auth lifecycle regression: `/api/v1/studio/auth/status` now reaches its status handler when a request retains a revoked session token after logout. The existing lifecycle test was not changed.
- Verification on the rebased tree: solution build passed; complete `TaskServer.Tests` passed (864/864); affected `OrchestratorApi.Tests` classes passed (82/82) with `Category!=MachineBound`; the guard filter passed in all eight .NET test projects (82 backend guards, no matches elsewhere); frontend build through the focused e2e launcher and `npm --prefix frontend run lint` passed; focused Playwright Product invitation spec passed (1/1), with `product-invitation.png` in the task results directory.
- An initial backend class run included a `MachineBound` benchmark and failed only because `TASK_CORE_BENCH_FIXTURE` was absent. The final rerun used the repository's normal machine-bound exclusion and passed.
