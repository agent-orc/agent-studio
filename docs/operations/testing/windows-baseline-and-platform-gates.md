# Windows test baseline and platform gates

The tested development baseline is Windows with Git Bash (see
[CONTRIBUTING.md](../../../CONTRIBUTING.md)). This page states what an unfiltered
`dotnet test` is supposed to look like there, what a `Skipped` result means, and
how to cover the skipped cases.

## The contract

`dotnet test agent-taskboard.sln` **without any platform filter** is green on
Windows. Tests that genuinely cannot run there report as `Skipped` with a reason,
never as `Failed` and never as silently filtered away.

That last part is the point. A filter (`--filter "Platform!=Linux"`) would also
produce a green run, but it hides how much was actually covered: the operator
sees "Passed: 900" either way. A gate keeps the number honest — `Skipped: 3` is
printed next to the reason, so the gap is visible on every run and nobody has to
remember to add a filter.

## Reading a skip

```
[xUnit.net] AgentRunner.Tests.DurableAgentProcessTests.Pid_with_a_different_worktree_is_not_adopted [SKIP]
            Linux-only: the worktree proof reads /proc/<pid>/cwd.
```

The reason names the concrete facility the test needs. Two reason prefixes exist:

- `Linux-only: …` — the behaviour under test is a Linux kernel or systemd
  facility (`/proc`, cgroups, `setsid`, unit files, Unix file modes). It has no
  Windows equivalent, and the production code usually skips the same check behind
  an `OperatingSystem.IsLinux()` guard.
- `No POSIX shell on this host …` — the behaviour is portable, the machine is
  merely missing an interpreter. This should not appear on a correctly set up
  baseline; install Git for Windows or put `bash` on the `PATH`.

## The rule when a test is red on Windows

Only one of these two answers is acceptable. "Add a filter" is not.

**Repair it** when the behaviour under test is portable and only an incidental
detail is Unix-specific — a hard-coded `/bin/sh`, a `C:\…` path handed to a shell
script that validates `[[ "$p" == /* ]]`, CRLF in a generated shebang, or a
`Directory.Delete(…, recursive: true)` that trips over git's read-only objects
under `.git/objects`. Use `AgentStudio.TestSupport.PosixShell`:

```csharp
FileName = PosixShell.RequirePath(),                 // a real interpreter path
start.ArgumentList.Add(PosixShell.ToShellPath(path)); // C:\Users\x -> /c/Users/x
```

`PosixShell` is the single place that knows where bash lives (PATH, the Git for
Windows install locations, or `AGENT_STUDIO_TEST_SHELL`) and how to spell a path
for it. Do not add a second lookup — that is exactly how a Windows fix ends up
working in one suite and not the next.

**Gate it** only when the behaviour itself is unavailable. Mirror the existing
`Category=MachineBound` convention: an explanatory comment, the trait, the gate.

```csharp
// Linux-only 02.08. (AGT-2472): the worktree half of the adoption proof reads
// /proc/<pid>/cwd; VerifyLive skips that check on Windows by design.
[SkippableFact]
[Trait(PlatformGate.TraitName, PlatformGate.Linux)]
public async Task Pid_with_a_different_worktree_is_not_adopted()
{
    PlatformGate.LinuxOnly("the worktree proof reads /proc/<pid>/cwd");
    ...
}
```

A test that is neither — one that shows a real defect — stays red. It is a bug
report, not a portability problem, and must not be gated.

## Covering the gated cases

The gated tests are not dead weight; they run for real on Linux, where
`PlatformGate.LinuxOnly` is a no-op and the case executes normally.

- **Remote runner host** — `agent-runner-01` is the production Linux host. See
  [remote-hosts.md](../remote-hosts.md) for connecting to it.
- **WSL** — sufficient for the `/proc` and file-mode cases, since it is a real
  Linux kernel:

  ```bash
  wsl dotnet test runner.Tests/AgentRunner.Tests.csproj \
      --filter "Platform=Linux"
  ```

  The `Platform` trait exists for exactly this direction — selecting the gated
  cases *in*, not filtering them out.
- **Release CI** — runs on Linux, so nothing is skipped for platform reasons
  there. The one filter release CI does apply, `Category!=MachineBound`, is
  unrelated: it removes tests that inspect a particular live checkout and are
  therefore not hermetic release evidence. See
  [releases.md](../releases.md#honest-ci-contract).

## Local build isolation

Local API instances keep file locks on the normal build output. Run tests with a
scratch artifacts path so a test build cannot collide with a running backend:

```bash
dotnet test agent-taskboard.sln -p:ArtifactsPath=/c/scratch/obj-tests
```

Use `ArtifactsPath`, not `BaseIntermediateOutputPath`.

## Temporary SQLite fixtures

Let the product store initialize its canonical schema. Do not repeat an already
integrated migration through a fixture-owned connection. When a test must open
an additional `Microsoft.Data.Sqlite` connection to a database inside a
temporary directory, set `Pooling=False` (or `Pooling = false` with
`SqliteConnectionStringBuilder`). Disposing a pooled connection can retain its
native file handle for the process lifetime. Linux permits the fixture directory
to be unlinked in that state, but Windows reports a sharing violation and turns
an otherwise passing test into a cleanup failure.
