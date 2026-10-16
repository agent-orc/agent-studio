# AGT-3167 delivery status

- Result SHA (SHA-256 of the source diff, excluding this status file): `1dc84cdef8d38b13bf20076d321dca9ae5f86629fd3a8a08b4e19c9f91922e02`.
- Base commit: `40fc92227765baba37a5d46537c9027f9f7fa33b`. The platform owns the delivery commit, so no result commit SHA exists in this worktree yet.
- Implemented the downgrade order: write the complete schema-6 primary and `.bak` atomically before moving either schema-7 journal. Crash injection covers archive conversion, both snapshots, and both journal moves; recovery from a damaged primary retains every committed attempt.
- A failed terminal compaction restores the in-memory task markers. A later sweep terminalizes an open run and review attempt.
- The task-list performance test injects the authority clock, and its fixed-date guard baseline entry was removed.
- Verification: `dotnet build agent-taskboard.sln --no-restore` passed; `dotnet build backend.Tests/OrchestratorApi.Tests.csproj --no-restore -m:1 /p:UseSharedCompilation=false` passed after the final edits; the focused class filter `FullyQualifiedName~AttemptAuthorityServiceTests|FullyQualifiedName~JobsEndpointPerfTests` passed 71 tests; the guard filter `FullyQualifiedName~.Architecture.|Category=Guard` passed on all eight .NET test projects (82 backend tests, no matches in the other seven); `npm --prefix frontend run lint` passed. `git diff --check` passed.
