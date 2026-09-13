# Windows Process Spawn Safety

Windows services and background workers often run without an attached console.
When such a process starts a console-subsystem child such as `git`, `cmd`,
`node`, `dotnet`, `powershell`, `codex`, or `claude`, Windows may allocate a new
visible console for the child. On Windows 11 this can appear as a Windows
Terminal window. Redirecting standard input, output, or error does not suppress
the window.

## Spawn rule

Every .NET `ProcessStartInfo` used by this repository must set
`UseShellExecute = false` and `CreateNoWindow = true`. Put `CreateNoWindow`
directly after `UseShellExecute` in an object initializer so the safety property
is visible during review. A shared factory may apply the property when every
call path is guaranteed to pass through that factory.

Every Node child-process call in `scripts/`, `frontend/tests/`, and
`update-service/` must pass `windowsHide: true` in its options. This applies to
`spawn`, `spawnSync`, `exec`, `execSync`, `execFile`, `execFileSync`, and
`fork`.

`DETACHED_PROCESS` on a parent takes precedence over `CREATE_NO_WINDOW`. Do not
start the Vitest or Playwright process host from a `DETACHED_PROCESS` parent.
Their upstream worker and process-host implementations are under
`node_modules`, which this repository does not patch.

## Redirected streams

Redirection creates a second independent safety obligation. Start reads of both
stdout and stderr before waiting for the child, then await both reads after the
child exits. A redirected stream that is never drained can fill its pipe buffer
and block the child indefinitely. On affected Windows Terminal profiles, a
non-zero or blocked child can also leave its visible terminal window open.

For asynchronous .NET code, use this order:

```csharp
var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
await process.WaitForExitAsync(cancellationToken);
var error = await stderrTask;
var output = await stdoutTask;
```

## Guard tests

`ProcessStartGuardTests` scans every C# source file under `backend/`, `runner/`,
`task-server/`, `update-service/`, `retention/`, `setup/`,
`orchestrator-engine/`, and `studio-bff/`. Test projects and `bin` and `obj`
outputs are excluded. It checks every `new ProcessStartInfo` initializer and
every `new Process` construction. A new unguarded site fails the backend test
gate with its file and line.

Run the .NET tripwire directly with:

```sh
dotnet test backend.Tests/OrchestratorApi.Tests.csproj \
  --filter FullyQualifiedName~ProcessStartGuardTests
```

The Node tripwire is `scripts/process-spawn-guard.test.mjs`. It scans `.mjs`
files in the Node rule roots, excludes `node_modules`, and runs as part of
`npm run lint:ci` from `frontend/`. Run it directly with:

```sh
node --test scripts/process-spawn-guard.test.mjs
```

## Allowlist policy

An exception is valid only when a shared caller or dependency applies the same
guard before the process starts. Add a narrow `file:line` entry to the allowlist
inside `ProcessStartGuardTests` and include a concrete reason naming that
shared enforcement point. The test rejects stale entries when the referenced
site moves or disappears. Do not allowlist a spawn because it is currently
unreachable, short-lived, test-only in practice, or expected to redirect its
streams.
