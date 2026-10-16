# AGT-3153 release workflow status

- Result SHA: `6727e676a465911612eccb8c1c3c7157ca548fc7` (one rebased delivery commit on `ba68ca8aa`).
- Implemented the operator's rollback option: stage all eight images at `sha-<commit>`, record previous `v<version>` and `latest` digests, promote those tags before creating the GitHub Release, and restore touched tags if promotion fails. The rollback test covers a failure after the fourth copy, including removal of newly created version tags.
- Kept AGT-3158's installation scenarios and deferred-step gate in the release candidate path. Workflow dispatch uploads the complete candidate and image artifacts without publishing.
- Operator step after publication: set each `ghcr.io/agent-orc/*` release package to **public** in GitHub Packages, then confirm anonymous `v<version>` pulls. The workflow reports anonymous visibility after release without failing a completed publication. Package visibility was not changed here.
- No release tag, GitHub Release, image, or remote ref was published by this task.
- GitHub API run `37255120101` confirms that v0.9.5 failed in Linux `Test .NET solution` and Windows `Publish win-x64 self-contained binaries`. The API log-download endpoint returned HTTP 403 without a token in this runner, so the detailed log text could not be inspected here.
- Verification: `dotnet build agent-taskboard.sln --no-restore` passed; the two changed backend test classes passed (29 tests); the architecture and guard filter passed (81 tests in the backend test project, no matching tests in the other seven .NET test projects); the release script contract suite, image promotion rollback test, workflow dependency test, installation scenario driver test, YAML parse, and `npm --prefix frontend run lint` passed.
