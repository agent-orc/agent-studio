---
id: provider-auth-indeterminate-latch-after-limit
title: "provider-auth stays indeterminate after a provider limit reset until the daemon restarts"
status: fixed
first-seen: 2026-10-13T00:00:00Z
last-seen: 2026-11-17T00:00:00Z
severity: blocker
category: runner
tags: [provider-auth, runner, quota, canary, review, claim-admission]
affects: [runner, task-server, backend]
related-tasks: [AGT-3119, AGT-3948]
related-adrs: []
---

# provider-auth stays indeterminate after a provider limit reset until the daemon restarts

**What.** On 17 November 2026 the Claude weekly limit was reached around
08:00Z and reset at 12:00Z. Every runner host kept logging
`provider auth probe completed provider=claude outcome=Indeterminate` once a
minute for hours after the reset, while `claude auth status --text` confirmed
an active session. Review claims stopped at 08:18Z and coding claims at
13:41Z (`lacks provider-auth:claude (probe indeterminate ...)`,
`Required capability 'provider-auth:claude' is held for indeterminate.`).
A plain daemon restart at 20:28Z cleared it. 13 October (AGT-3119) had the
same pattern.

**Why.** Only a real minimal request can clear an indeterminate binding; the
status command proves the login, not the provider. After the limit's
unknown-reset window expired (60 minutes), every permitted refresh sent a real
request. The limit answer (`You've hit your weekly limit · resets 12pm`) is
not a classified quota answer, so each request read as indeterminate and kept
the server's circuit granting canaries, about one real request per minute.
The daily budget of 48 real requests was gone within the limit window. After
the reset the probe had no request left until the budget window rolled over a
day later, and every successful status command kept the inherited
indeterminate outcome. A restart reset the in-memory budget.

**Diagnosis.** On the host, the runner log shows the probe line once a minute
and no `provider-auth recovery request` lines. Execution Hosts shows the
provider-auth key held `probe-indeterminate` with the status-command detail.
Since AGT-3948, `GET /api/runner/auto-review-queue` reports
`stagnationCause: provider-auth-latched` with `operatorAction`, and
`GET /api/runner/queue-starvation` names the same cause on each refused card,
once the binding has been indeterminate behind an active session for 30
minutes.

**Recovery.** Restart the runner daemon on the named host
(`systemctl restart agent-runner.service` / `agent-runner-review.service`,
or `docker restart` for a containerized review daemon). The fresh process
reports `provider-auth status=ready` and claims at once.

**Fixed behavior (AGT-3948).** A binding whose outcome is anything other than
a proven success gets its own paced recovery request, also while the status
command is degraded and without a canary permit. After a failed request the
next one waits 5, 10, 20, then at most 30 minutes; a reported reset within
that ceiling schedules the request at the reset itself. Limit answers are
refunded to the daily budget, and a spent budget still allows one recovery
request per 30 minutes, so the request after the reset always remains. The
Task Server extends the owner's 45-second canary window when the runner
reserves again right before its real request, so that success releases the
hold. The advertisement carries `statusSessionActive` and `since`, which the
queue watchdogs use to name the latch and the restart.
