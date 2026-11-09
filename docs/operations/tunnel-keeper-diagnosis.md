# Reverse-Tunnel Keeper: Death Diagnosis (2026-08-23)

Operator investigation into why the SSH reverse tunnel that connects the remote
runner host `agent-runner-01` to the local Task Server keeps dying. Five stalls
in eight days, each of which silently froze both runner planes (coding +
review) because the runner reaches the Task Server *only* through this tunnel.

## 1. Inventory (what actually runs)

| Thing | Finding |
| --- | --- |
| Scheduled task | `AgentRunner-TunnelKeeper`, State `Ready`, author `TUFI2\rmisc`, principal `rmisc` (Interactive, RunLevel Limited). |
| Command it runs | `powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File %USERPROFILE%\ops\tunnel-keeper.ps1`; **not** the repo copy under `deploy/windows/agent-runner-tunnel/`, and **not** the old `reverse-tunnel.ps1` in the devspace root (that loop-with-backoff script is dormant). |
| Trigger | Single time trigger (2026-08-02 17:38) with a 5-minute repetition, infinite duration. **No AtStartup trigger.** `StartWhenAvailable = False`. |
| Battery policy | `DisallowStartIfOnBatteries = True`, `StopIfGoingOnBatteries = True`, `WakeToRun = False`. This host is a **laptop** (battery `A32-K55`, Wi-Fi-only `Realtek 8822CE`, S3 standby + Fast Startup). |
| Keeper log | `%USERPROFILE%\ops\tunnel-keeper.log` (478 lines at time of writing). Journals incidents only; healthy runs exit silently. |
| Forwards maintained | `-R 15031:127.0.0.1:5031`, `-R 5031:127.0.0.1:5031`, `-R 4011:localhost:4011`. Runner-side endpoint the daemons use: `127.0.0.1:15031`. |
| Prepared watchdog | `deploy/windows/agent-runner-tunnel/{register-tunnel-watchdog.ps1, tunnel-watchdog.sh}` exists but **is NOT registered** — `Register-ScheduledTask` needs admin, which is not available on this host. `Microsoft-Windows-TaskScheduler/Operational` log is also **disabled** (enabling needs admin), so per-run task history could not be read. |
| Runner uptime | `up 46 days`. The runner host never rebooted in the window — **every outage is on the Windows control-plane side.** |

Evidence sources used: the keeper log; Windows `System` / `Kernel-Power` /
`Power-Troubleshooter` / `WLAN-AutoConfig` event logs; and the runner-side
`agent-runner.service` journal. Note two evidence gaps: (a) the `agent` user on
the runner is not in `adm`/`systemd-journal`, so **sshd's own disconnect
reasons (`journalctl -u ssh`, auth.log) are not readable** from our access
level; (b) the runner journal only retains back to **2026-08-22 08:05**, so
runner-side confirmation exists only for the most recent stall. Earlier stalls
are dated from the keeper log + Windows events.

## 2. Evidence table

All times are host-local (Europe/Berlin), which equals the runner's CEST. The
keeper log wall-clock is briefly wrong right after a wake until NTP resync (the
Windows log shows multi-hour "system time changed" deltas on each resume).

| Date / window | Keeper-log signature | Correlated Windows / runner evidence | Reading |
| --- | --- | --- | --- |
| **08-12 ~02:42 → 11:43** (#1) | `02:42:03 Strecke tot … neu aufgebaut`, then a **9 h gap with no terminal line**; recovers `11:43 gesund`. | No keeper runs logged for 9 h despite 5-min cadence. | Truncated run + suppressed ticks: host asleep / on battery, keeper cannot run, so the blip persists until it is awake+charging. |
| **08-12 22:43 → 23:41** (#2) | `22:43:51 Strecke tot … stale pid=42264 beendet` (no `gesund`); recovers `23:41:15`. | SessionUnlock transition 22:58. | Same shape: rebuild started but truncated by a sleep before it could confirm. |
| **08-13 02:31 → ~11:08** | `10:33–11:03 Lokales Backend antwortet nicht`; `11:08 gesund`. | `1074` MoUsoCoreWorker + TrustedInstaller **Windows Update double reboot** 02:31 & 02:33; `Kernel-Power 109` reboot. | WU reboot killed the tunnel; the local Task Server was also down until ~11:08, and the keeper correctly abstained ("backend not judgeable") until it returned. |
| **08-16 00:18 → 11:13** (#3, the big one) | **~11 h wall of bare `Strecke tot … neu aufgebaut`** every 5 min, **never** a `stale … beendet`, `nicht frei`, `gesund`, or `fehlgeschlagen` line; recovers `11:13:16`. | WLAN roams `memucho.de → mem → FerienhausLychen`; no successful association from ~00:15 until `11:12 8001 connect FerienhausLychen`. Prior sleep 08-15 11:37 → wake 08-16 00:11. | **Off-network + keeper self-crash.** With no path to Hetzner, the keeper's *unguarded* `& ssh …` in the port-release loop threw a terminating error each tick (see §3), so it could neither heal nor even log a failure. Cleared only when Wi-Fi reattached to a reachable network. |
| **08-19 22:55 → 08-20 17:19** (#4) | `08-19 21:48 tot→gesund` (fast), then `08-20 17:27 tot→17:27 gesund`. | Dense sleep/resume "Button or Lid" cycles 08-19 21:32–22:55; **long low-power state 08-19 22:55 → resume 08-20 17:19** (~18 h, `Power-Troubleshooter` wake). | Tunnel dead for the whole ~18 h the laptop slept; keeper cannot run while asleep. First tick after wake healed it in 9 s. |
| **08-21 21:04 → 08-23 09:33** (#5, the reboot) | `08-20 23:20 Strecke tot … stale pid=53472 beendet` (no follow); then silence to `08-23 09:18 backend nicht … 09:33 gesund`. | `Kernel-Power 41` **dirty reboot** at 08-23 09:14; `6008` "previous shutdown at 9:04:50 PM on 8/21/2026 was **unexpected**". | Host went to sleep/off 08-21 ~21:04 and came back on 08-23 via an unclean boot. Tunnel down the entire time; keeper resumed once the backend was up (~09:33). |

Additional recurring signature, not tied to a single death:
`Remote-Listener 15031 … auch nach sshd-Kill nicht frei` (08-10, 08-18). This is
the **zombie-listener** path — and it always failed, because the sshd-kill it
attempted was a no-op (see §3, finding C).

## 3. Root cause

The tunnel does not die from sshd idle timeouts. On the runner, `sshd_config`
leaves `ClientAliveInterval`/`TCPKeepAlive` at defaults (0 / commented), and the
keeper's client already sends `ServerAliveInterval=15`. There is no sign of the
server reaping healthy sessions.

**Primary cause — the control plane is a roaming Wi-Fi laptop that sleeps.**
Every S3 standby, lid/button sleep, Wi-Fi roam, Windows-Update reboot, and the
one unclean reboot tears down the ssh session. That alone would be survivable —
the keeper is built to re-establish within seconds and usually does — but two
keeper defects convert short blips into multi-hour stalls:

- **(A) The keeper cannot run when it is most needed.** The task has
  `DisallowStartIfOnBatteries = True`, `StopIfGoingOnBatteries = True`,
  `WakeToRun = False`, and no AtStartup trigger. While the laptop sleeps or runs
  on battery, the 5-minute keeper simply does not fire, so any death that begins
  during sleep/battery persists until the host is awake *and* on AC. This is the
  shape of stalls #1, #4, #5.

- **(B) The keeper crashes silently when the network is down.** The script sets
  `$ErrorActionPreference = 'Stop'`. Two `& ssh … 2>$null` calls in the
  port-release loop (and the sshd-kill call) were **not** wrapped in try/catch.
  In PowerShell 5.1, when `ssh.exe` fails to connect it writes to stderr; under
  `Stop` that stderr becomes a terminating `NativeCommandError` and aborts the
  script mid-run — after the `Strecke tot` line but before any recovery or any
  `Neuaufbau fehlgeschlagen` line. This is the exact fingerprint of stall #3:
  ~130 consecutive bare `Strecke tot` lines with no terminal line, for 11 hours.
  Reproduced directly: the old unguarded pattern **threw**
  `System.Management.Automation.RemoteException` against an unreachable host,
  while `Test-TunnelHealthy` (which *is* wrapped) survives.

- **(C, contributing) The zombie-listener cleanup was a no-op.** After an abrupt
  host disappearance the runner's sshd keeps the `15031` forward bound
  ("zombie listener"); a fresh `ssh -R` then hits `ExitOnForwardFailure` and
  cannot rebind. The keeper tried to clear it with
  `sudo -n ss … | xargs -r sudo -n kill`, but the `agent` user's sudoers grant
  covers only specific `systemctl` / `agent-runner-deploy` commands — **not**
  `ss`/`kill`. Verified live: `sudo -n ss` returns *"sudo: a password is
  required"*. So every `… auch nach sshd-Kill nicht frei` line was this failing
  path. The listener only ever cleared when sshd's own TCP stack eventually
  timed out (there is no `ClientAliveInterval` to speed that up) or the operator
  intervened.

Net: sleep/roam/reboot **starts** each outage; defects A–C decide whether it is
a 10-second blip or an 11-to-18-hour stall.

## 4. Applied hardening (live keeper, non-disruptive)

Edited in place: `%USERPROFILE%\ops\tunnel-keeper.ps1` (original backed up to
`tunnel-keeper.ps1.bak-20260823`). The scheduled-task invocation is unchanged
(`-File …\tunnel-keeper.ps1`, no new args), so nothing about registration moved.
All three forwards (15031 / 5031 / 4011) are preserved.

1. **Guarded ssh (fixes cause B).** New `Invoke-KeeperSsh` helper runs every
   remote ssh call with `$ErrorActionPreference='Continue'` and `2>&1` capture,
   returning `{Output, ExitCode}` and never throwing. The port-release loop and
   the sshd-kill now use it. When the runner is unreachable the keeper logs
   `Remote nicht erreichbar (ssh exit=…)` and exits cleanly instead of dying
   mid-run — so future off-network windows are self-reported, not silent.
2. **ssh exit-code / stderr capture (logging ask).** The replacement forward is
   started with `LogLevel=VERBOSE` and `-RedirectStandardOutput/Error` to
   timestamped `ssh-YYYYMMDD-HHMMSS.{out,err}.log` files; a line is written to
   `tunnel-keeper-ssh.log`. If the forward exits early, its exit code and the
   last stderr lines are folded into the keeper log — so "remote port forwarding
   failed" style causes become visible.
3. **Log rotation.** `tunnel-keeper.log` rotates to `.1` past 1 MB.
4. **Zombie cleanup without sudo (fixes cause C).** The sshd-kill path now
   extracts the listener PIDs the `agent` user can already see
   (`ss -H -ltnp "sport = :15031"`) and `kill`s them directly (then `-KILL`).
   The holding sshd is owned by the `agent` user (uid 1000); `kill -0` against it
   is permitted — verified live — so no sudo is required. This runs only after a
   failed probe and after the local stale ssh has been stopped, and targets only
   port 15031.

### Verification

- **Parser:** `Parser::ParseFile` → *SYNTAX OK*.
- **Guard dry run (against an unreachable host, live tunnel untouched):** the new
  helper returned `ExitCode=255` and execution continued past the call — where
  the old pattern threw. Confirmed "SCRIPT SURVIVED unreachable ssh under
  EAP=Stop".
- **Live run (exactly as the task invokes it):** exit `0`, 4 s, keeper log grew
  0 bytes, the existing forward PID `19156` was left in place — healthy path is
  still a silent no-op. Nothing was in flight (`claimed last 10 min: 0`) before
  the run.
- **End-to-end after the change:**
  `ssh agent-runner 'curl -sf http://127.0.0.1:15031/healthz'` → `HEALTHZ_OK`;
  `/api/tasks` → `API_TASKS_OK`.

What was **not** done, deliberately: no new scheduled task was registered (no
admin); the aggressive zombie-kill was not exercised against the live healthy
tunnel; no runner service was restarted (read-only journal queries only).

## 5. Recommended follow-ups (mapped to existing cards — no duplicates)

The two obvious "build a watchdog / productize it" cards already exist; this
diagnosis feeds them rather than spawning new ones.

- **AGT-2658 — "Tunnel self-healing watchdog: detect dead reverse tunnel, kill
  zombie, restart keeper"** (state `7-archive`). Its deliverables
  (`tunnel-watchdog.sh`, `register-tunnel-watchdog.ps1`) are already in the repo
  but **unregistered for lack of admin**, and two of its assumptions are now
  known to be wrong on this host and should be corrected before it is trusted:
  (i) its `kill_remote_listener` relies on the `agent` user seeing/killing the
  listener PID — which works only because the sshd is agent-owned (document that;
  it is *not* a sudo path); (ii) registering it as an S4U task still needs an
  administrator. Recommend: when admin is next available, register the watchdog
  **and** fix the keeper task's power policy — clear `DisallowStartIfOnBatteries`
  / `StopIfGoingOnBatteries` and add an AtStartup trigger + `StartWhenAvailable`
  (this is what `register-tunnel-keeper.ps1` already does; the *live* task
  predates it). That directly removes cause A.

- **AGT-2664 — "Tunnel watchdog belongs to the product: install via Agent Studio
  host setup"** (state `5-human-review`). The productized keeper/watchdog it
  installs should carry the three hardenings applied here (guarded native ssh,
  ssh exit/stderr capture, no-sudo zombie kill) and should not assume a
  sudo-capable `ss`/`kill`. Fold the §3 findings into its host-setup checklist,
  and consider a server-side `ClientAliveInterval` on the runner's sshd so the
  zombie listener self-clears without any client-side kill.

No new cards are proposed; the gaps above belong inside AGT-2658 and AGT-2664.
