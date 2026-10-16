# AGT-3155 Linux installer review status

- Review fix: removed the unused `scripts/release/install.sh` copy. The root `install.sh` remains the script packaged and checksummed for releases.
- Current checkout SHA: `bf7ec1148224e2b9ce1c0a2a243b0297a10c4ed2`. The result SHA for this uncommitted review fix will be assigned by the platform-owned delivery commit.
- Verification passed: `dotnet build agent-taskboard.sln --no-restore`; `LinuxInstallerTests` (14 tests); the architecture and guard filter across all eight .NET test projects (81 matching tests passed); `scripts/release/linux-install-script.test.sh`; `scripts/release/release-scripts.test.sh`; and `npm --prefix frontend run lint`.
- No release tag, GitHub Release, package visibility change, or remote ref was published by this review fix. Publishing the release and setting GitHub Packages visibility remain operator steps.
