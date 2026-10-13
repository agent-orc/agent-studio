---
id: localhost-ipv6-connect-delay
title: "Every new connection to localhost:<port> waits 0.2 to 2 s because only 127.0.0.1 listens"
status: mitigated
first-seen: 2026-10-12T19:30:00Z
last-seen: 2026-10-12T20:06:00Z
severity: minor
category: misc
tags: [localhost, ipv6, loopback, happy-eyeballs, latency, frontend, api]
affects: [frontend/scripts/serve-prebuilt-frontend.mjs, api.sh, frontend/e2e/helpers/api.ts, frontend/proxy.conf.json, scripts/generate-project-map.mjs, docs/operations/stable-release-contract.md]
related-tasks: [AGT-3092, AGT-2862, AGT-2754, AGT-2984]
related-adrs: []
---

# localhost-ipv6-connect-delay

**What.** On the Windows Stable host, every new TCP connection to `localhost`
on a port that only listens on `127.0.0.1` paid a fixed delay. Measured against
the running Stable on 12.10.2026 (finding `ui/api-6`, re-measured at 20:06Z):

| Client and URL | via `localhost` | via `127.0.0.1` |
|---|---|---|
| curl `time_connect`, `:5031/healthz` | 0.205-0.214 s | 0.0009-0.0014 s |
| curl `time_connect`, `:4011/` | 0.203-0.214 s | 0.0009-0.0013 s |
| Windows PowerShell 5.1 `[Net.WebRequest]`, one request per fresh process, `:5031/healthz` | 2.06-2.09 s | 0.03-0.04 s |
| Node 24 connect time (`net.connect`, `http.get`), `:5031` and `:4011` | 3-7 ms | 2-6 ms |

The first finding run measured 0.204 to 0.221 s with curl on `localhost` and
under 1 ms on `127.0.0.1`. `netstat -ano` showed only `127.0.0.1:5031` and
`127.0.0.1:4011` LISTENING; nothing listened on `[::1]`. The cost is per new TCP
connection, not per request, and hit every browser session on
`http://localhost:4011`, every operator or agent `curl` against
`localhost:5031`, and every PowerShell process that called `localhost:5031`.

**Why.** Three facts combine.

1. `localhost` resolves to `::1` first. Node `dns.lookup('localhost', { all: true })`
   returns `::1`, then `127.0.0.1`.
2. On this host a connect to a closed `[::1]` port is refused only after about
   2 s (curl 2.03 s, .NET `TcpClient` 2.25 s). curl starts its IPv4 attempt
   after its 200 ms Happy Eyeballs delay. .NET Framework tries the addresses
   one after another and pays the full 2 s, once per process and host:port.
   Node gets the refusal in 2-3 ms and loses nothing measurable. Browsers use
   their own fallback timer, which was not measured.
3. The backend cannot simply add `[::1]`. `[::1]:5031` is reserved for the
   connector: `backend/Features/Connector/ConnectorHost.cs` binds
   `IPAddress.IPv6Loopback:5031` and refuses any other URL, and
   `ConnectorOptions.cs` pins the authority `[::1]:5031`. Browsers in turn must
   keep `localhost`: with default configuration the connector accepts only the
   origins `http://localhost:4011` and `http://[::1]:4011` and rejects
   `http://127.0.0.1:4011`, while the Studio BFF accepts `127.0.0.1:4011` and
   `localhost:4011`. `http://localhost:4011` is the only origin both accept.

**Workaround.** The rule, verbatim:

> Studio ports serve both loopbacks and browsers open http://localhost:<port>; backend 5030/5031 binds 127.0.0.1 only and API clients spell 127.0.0.1.

When you type a backend URL into `curl` or PowerShell, write
`http://127.0.0.1:5031`, not `http://localhost:5031`.

**Long-term.** Mitigated in AGT-3092. The prebuilt frontend server
(`frontend/scripts/serve-prebuilt-frontend.mjs`, `startFrontendServer`) binds
`127.0.0.1:<port>` and then `[::1]` on the same port, with one shared request
and upgrade handler, so a browser on `localhost` connects on its first
attempt. A host without an IPv6 loopback logs one warning and keeps serving
IPv4; any other `[::1]` bind failure, such as a taken port, fails the start.
`api.sh` keeps the backend on `127.0.0.1` with a comment naming the connector
as the owner of `[::1]:5031`, and in-repo API clients spell `127.0.0.1`.
`ng serve` is still IPv4 only (`host: 127.0.0.1` in `frontend/angular.json`,
AGT-2862), so a browser on `http://localhost:4010` still pays the fallback
against the dev server.
