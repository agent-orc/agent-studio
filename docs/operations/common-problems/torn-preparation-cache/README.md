---
id: torn-preparation-cache
title: "Merge gate reports a build failure after a preparation cache loses NuGet package files"
status: fixed
first-seen: 2026-09-24T18:33:00Z
last-seen: 2026-09-25T07:26:00Z
severity: blocker
category: runtime
tags: [merge-gate, preparation-cache, nuget, environment, retry, windows]
affects:
  - contracts/TaskServer.Contracts/ProjectPreparation.cs
  - backend/Features/Pipeline/BuildTestGateRunner.cs
  - backend/Features/Pipeline/MergeIntoDevelopRunner.cs
  - backend/Features/Pipeline/GateEnvironmentRetry
related-tasks: [AGT-2826, AGT-2873, AGT-2880, AGT-2888, AGT-2901]
related-adrs: []
---

# torn-preparation-cache

**Symptom.** Remote Review passed, but the merge gate fails in
`NuGet.targets` with `Could not find file` for a `.nupkg` below
`agentstudio-preparation-cache/.runs/<runId>/nuget/`. The integration must show
`gate-environment-failure`, not `build-gate-failed`.

**Cause.** The gate copied a published NuGet block whose package directories
still contained extracted assemblies and `.sha512` files but had lost NuGet's
`.nupkg.metadata` extraction markers and package archives. A plain non-empty
directory check accepted that torn tree.

**Automatic recovery.** Cache lookup now checks every NuGet
`<package>/<version>` directory for `.nupkg.metadata`. A failed validation is
logged with its block key, the published entry is evicted, and preparation
continues from an empty private block. Empty successful blocks are not
published. If NuGet still reports a missing package from the gate's own private
run directory, the gate classifies the result as an environment failure and
evicts the published NuGet block defensively.

The automatic gate-environment retry sweep can then replay integration of the
unchanged delivery SHA while reusing its passed review. An operator can request
the same replay immediately with:

```http
POST /api/tasks/{key}/integration/retry
```

The route returns the replay result and does not create another review round.
It refuses product build failures and deliveries without a passed review for
the current SHA.

**Check.** Look for both cache validation and typed integration evidence:

```text
project-prepare cache validation block=nuget key=<key> state=invalid reason=nuget-metadata-missing:<package>/<version>
project-prepare cache block=nuget key=<key> state=evicted reason=validation-nuget-metadata-missing:<package>/<version>
gate-environment-failure
```
