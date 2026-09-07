---
id: claude-launcher-stub-after-autoupdate
title: "claude CLI not available right after a CLI auto-update"
status: fixed
first-seen: 2026-09-06T07:44:00Z
last-seen: 2026-09-06T16:32:00Z
severity: blocker
category: cli
tags: [claude, cli, npm, launcher, postinstall, quota-probe, self-heal, windows]
affects: [backend/Features/Cli/Pty, backend/Features/Cli/Quota, backend/Features/Cli/Repair]
related-tasks: [AGT-2706]
related-adrs: []
---

# claude-launcher-stub-after-autoupdate

**Symptom.** Every Claude quota probe fails with `claude CLI not available`, and
model discovery logs `Claude PTY model discovery failed; falling back to registry
catalog`. On the host, `claude --version` prints `Error: claude native binary not
installed.` The failure starts immediately after a probe whose raw sample shows
`Auto-updating…` and does not heal on its own: on 2026-09-06 eight probe cycles
between 07:45Z and 16:31Z failed in a row, and no coding run on that host could
have spawned the CLI either.

**Cause.** Two faults in sequence.

1. The PTY probes spawned the CLI without an updater guard. A quota probe or a
   model discovery must only observe the installed CLI, but an interactive start
   triggers the Claude Code self-updater, so the probe upgraded the global npm
   package underneath every later run.
2. Claude Code 2.1.263 is a launcher package. Its `install.cjs` postinstall
   hard-links (or copies) the platform binary from the optional dependency
   `@anthropic-ai/claude-code-win32-x64` over a 500-byte placeholder
   `bin/claude.exe`. The native package was extracted, but the postinstall did
   not replace the placeholder. Package directory and `claude.cmd` shim both
   looked healthy, so the self-heal classified the install as
   `PackagePresentWithShim`, ran no repair, and `logs/cli-self-heal.jsonl`
   stayed empty.

**Diagnosis.** On the affected host:

```powershell
claude --version
Get-Item "$env:APPDATA\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe" | Select-Object Length
Get-ChildItem "$env:APPDATA\npm\node_modules\@anthropic-ai\claude-code\node_modules\@anthropic-ai"
```

A `bin/claude.exe` of a few hundred bytes beside an extracted `*-win32-x64`
package is the launcher stub. `%LOCALAPPDATA%\npm-cache\_logs\*-debug-0.log`
names the update that produced it.

**Heal command.** Re-run the npm postinstall in the package directory; that is
exactly what npm skipped:

```powershell
cd "$env:APPDATA\npm\node_modules\@anthropic-ai\claude-code"
node install.cjs
claude --version
```

The launcher is repaired when `claude --version` prints the version and the file
is no longer a few hundred bytes. Falling back to
`npm install -g @anthropic-ai/claude-code@<installed version>` reinstalls the
same version including its postinstall.

**Fixed behavior (AGT-2706).** Every `PtySession.SpawnAsync` call passes
`CliEnvironment.ProbeEnvironment()`, which sets `CLAUDE_CODE_DISABLE_AUTOUPDATER=1`
and `DISABLE_AUTOUPDATER=1`, so no probe or discovery can update the global CLI;
`PtyProbeUpdaterGuardTests` pins that at every spawn site.
`LocalCliRepairService` recognises the launcher stub as its own install state,
journals the detection evidence (launcher size, nested native package, failing
`--version` text), repairs it by re-running `install.cjs` with the version-pinned
global install as fallback, and projects the stub into runner status as an
active failure until a healthy probe clears it. The existing
one-attempt-per-CLI-per-hour budget and the resolved-journal behaviour are
unchanged.
