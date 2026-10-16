# AGT-3155 Linux installer review fix

- Result SHA: `765bcc1566c8e575795b763b0805cd4433af8906` (duplicate-script removal, rebased onto `86878facd38f190124627338d2bcc986c3b36852`).
- Removed the unreferenced `scripts/release/install.sh`. Release packaging continues to use the verified root `install.sh`.
- Verification passed: `dotnet build agent-taskboard.sln --no-restore`; `sh scripts/release/linux-install-script.test.sh`; `sh scripts/release/release-scripts.test.sh`; setup tests filtered to `LinuxInstallerTests|InstallationJourneyTests` (86 passed); architecture and guard filter on all eight .NET test projects (82 matching backend tests passed); `FullyQualifiedName~Architecture` in backend (103 passed) and Task Server (5 passed); `npm --prefix frontend run lint`; `git diff --check`.
- No release tag or GitHub Release was created, and package visibility was not changed. Publishing a release and setting package visibility remain operator steps.

## AGT-3157 Windows Studio and Linux Agent Host integration

- Result implementation SHA: `51541562aeb32378f1823e7ae2a25f55757af313`, rebased linearly from accepted delivery `2afeae2f1` onto `origin/develop` at `acb056b20`.
- Kept the Linux one-command `install.sh` download and checksum verification while passing `--join --join-token-file` through to setup. Preserved single-use enrolment recovery with a protected host credential and join receipt saved before service installation, and per-host Windows tunnel keeper task names.
- Restored the route-specific preflight policy: joining over a reverse tunnel, LAN, or HTTPS does not require WireGuard. Updated `multi-machine.md` to describe the current join file, four Linux archives, and retry behavior.
- Verification: setup test suite passed (183); `IdentityBootstrapTests` passed (63); backend and Task Server `FullyQualifiedName~Architecture` filters passed (103 and 5); backend architecture and guard filter passed (82); the same guard filter completed on all eight .NET test projects, with no matches outside backend. All eight .NET test projects built. `scripts/release/linux-install-script.test.sh`, `scripts/release/release-scripts.test.sh`, and `npm --prefix frontend run lint` passed.
- Operator steps remain: verify the Windows and Linux join on real machines, then publish a release and change package visibility only through the release program's operator process. No tag, release, package visibility, or remote ref was changed here.
