---
id: open-http-request-at-verify-from-chat-polling
title: "Open HTTP request at verify() from background chat polling"
status: fixed
first-seen: 2026-09-29T00:00:00Z
last-seen: 2026-10-03T00:00:00Z
severity: blocker
category: ui
tags: [angular, http-testing, polling, vitest, flaky-test]
affects:
  - "orchestrator-side-sheet.pin.spec.ts"
  - "npm --prefix frontend run test:ci"
related-tasks: [AGT-3007]
related-adrs: []
---

# Open HTTP request at verify() from background chat polling

**Symptom.** `HttpTestingController.verify()` sometimes reports an open
`GET /api/runner/project-chat/status` in the side-sheet pin spec. The failure
depends on host load because the waiting child polls every two seconds while a
chat send is pending.

**Test pattern.** Use fake `setInterval` and `clearInterval` in a spec that
tests navigation or sending without testing the polling cadence. Destroy the
fixture or reset `TestBed` before restoring real timers. In the poller's own
spec, advance the clock explicitly, expect and flush the status request, then
verify that sending completion and component destruction stop further polling.
