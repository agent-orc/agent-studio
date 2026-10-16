# AGT-3155 Linux installer review fix

- Result SHA: `765bcc1566c8e575795b763b0805cd4433af8906` (duplicate-script removal, rebased onto `86878facd38f190124627338d2bcc986c3b36852`).
- Removed the unreferenced `scripts/release/install.sh`. Release packaging continues to use the verified root `install.sh`.
- Verification passed: `dotnet build agent-taskboard.sln --no-restore`; `sh scripts/release/linux-install-script.test.sh`; `sh scripts/release/release-scripts.test.sh`; setup tests filtered to `LinuxInstallerTests|InstallationJourneyTests` (86 passed); architecture and guard filter on all eight .NET test projects (82 matching backend tests passed); `FullyQualifiedName~Architecture` in backend (103 passed) and Task Server (5 passed); `npm --prefix frontend run lint`; `git diff --check`.
- No release tag or GitHub Release was created, and package visibility was not changed. Publishing a release and setting package visibility remain operator steps.
