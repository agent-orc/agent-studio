# AGT-3153 release workflow status


- Result SHA: `6727e676a465911612eccb8c1c3c7157ca548fc7` (one rebased delivery commit on `ba68ca8aa`).
- Implemented the operator's rollback option: stage all eight images at `sha-<commit>`, record previous `v<version>` and `latest` digests, promote those tags before creating the GitHub Release, and restore touched tags if promotion fails. The rollback test covers a failure after the fourth copy, including removal of newly created version tags.
- Kept AGT-3158's installation scenarios and deferred-step gate in the release candidate path. Workflow dispatch uploads the complete candidate and image artifacts without publishing.
- Operator step after publication: set each `ghcr.io/agent-orc/*` release package to **public** in GitHub Packages, then confirm anonymous `v<version>` pulls. The workflow reports anonymous visibility after release without failing a completed publication. Package visibility was not changed here.
- No release tag, GitHub Release, image, or remote ref was published by this task.
- GitHub API run `37255120101` confirms that v0.9.5 failed in Linux `Test .NET solution` and Windows `Publish win-x64 self-contained binaries`. The API log-download endpoint returned HTTP 403 without a token in this runner, so the detailed log text could not be inspected here.
- Verification: `dotnet build agent-taskboard.sln --no-restore` passed; the two changed backend test classes passed (29 tests); the architecture and guard filter passed (81 tests in the backend test project, no matching tests in the other seven .NET test projects); the release script contract suite, image promotion rollback test, workflow dependency test, installation scenario driver test, YAML parse, and `npm --prefix frontend run lint` passed.

# AGT-3159 board read latency status

- Result implementation SHA: `5ca77f8905b7fc0dd62462ead0a1a9d341ebbb4b`, rebased on `d876bde9061b480c17a978da851aba89f0c10554`. The status commit SHA is recorded in the delivered `results/status.md` after this file is committed.
- Implemented option: serve the last consistent task index, authority, and client identity snapshots while writers persist; coalesce invalidations into background index refreshes. Board trace now separates `compose.identity` and other compose sub-stages.
- Before, on stable `8a0b58b09`: observed `GET /api/tasks` latencies were 18, 166, 120, up to 304, and 64 seconds during write bursts. In one 64,530 ms trace, compose took 63,587 ms. The observed full scan took 260 to 530 ms and ran several times per second.
- After, in the isolated `DirtyIndex_BoardReadsServeThePublishedSnapshot_WithoutRunningOrAwaitingTheRefresh` test: 90 grouped reads completed during a 1,755.257 ms write interval with 8 creations, 1 continue, 7 sidecar writes, and 7 claims. The slowest measured read was 20.951 ms; 6 full scans covered the 23 writes. Across 93 board traces from that test, the maximum `compose` stage was 8.592 ms and `compose.identity` was 0.150 ms. These test measurements are not a production load comparison.
- Verification: the focused burst test passed; the five changed backend test classes passed (135 tests), and the lease and trace classes passed (19 tests). The backend architecture and guard filter passed (81 tests), including the test clock guard. The same filter completed in the other seven .NET test projects with no matching tests. The backend and operations-server test project builds passed, and the solution build built the other six test projects before it was stopped while waiting on the host's shared compiler. `npm --prefix frontend run lint` passed.

# AGT-3200 workspace deployment dashboard status

- Result source SHA-256: `e6b110eac401cdd3d2c35f82b77edaa1ca93637f3606f135fa8adcf4c27aeb8f` (the frontend source and test tree over base commit `2c4ec9e8c62e55f1b88763442277e426aab862a4`; the managed platform creates the delivery commit after this run).
- Implemented the Dossier's recommended BD-D1 option A for the workspace dashboard entry point and BD-D2 option A for its two-environment matrix slice. Each project has Staging and Production status from a typed fixture until BD-1 provides the API. Cells open the project's existing Deployment rail.
- The dashboard labels all fixture values as sample data. Group and environment filters, release, age, deployment state, health, approvals, and newer-release indicators are present.
- Verification: solution and frontend builds passed; `npm --prefix frontend run lint` passed; the focused dashboard component class passed (2 tests); the architecture/guard filter passed in backend (82 tests), runner (no matches), and Task Server (no matches); the focused mocked Playwright spec passed and saved dark and light screenshots under the task results directory.
