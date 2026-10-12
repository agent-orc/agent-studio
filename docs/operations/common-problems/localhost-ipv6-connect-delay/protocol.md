# Root-cause protocol

## Reproducer

```sh
curl -s -o /dev/null -w '%{time_connect}\n' http://localhost:4011/
curl -s -o /dev/null -w '%{time_connect}\n' http://127.0.0.1:4011/
netstat -ano | grep -E ':(4011|5031) .*LISTENING'
```

Before the fix the first line prints about 0.2 s and the second under 0.002 s,
and `netstat` lists only `127.0.0.1` listeners.

## Resolution order and refusal time

- Node `dns.lookup('localhost', { all: true })` returns `::1`, then `127.0.0.1`.
- A connect to the closed `[::1]:5031` is refused after about 2 s on this host
  (curl 2.03 s, .NET `TcpClient` 2.25 s). Node sees the refusal in 2-3 ms.
- curl races IPv4 after 200 ms (Happy Eyeballs), so it pays 0.2 s.
- .NET Framework tries addresses sequentially and pays 2 s, once per process
  and host:port.

## Why the backend stays on IPv4

`[::1]:5031` is the connector's authority (`ConnectorHost.cs`,
`ConnectorOptions.cs`). A backend bound to `[::1]:5031` would collide with it.
The connector does not resolve `localhost` for its own upstream either: a
clear-text local upstream must be a literal loopback address.

## Verification of the mitigation

`frontend/scripts/serve-prebuilt-frontend.test.mjs` asserts that both
loopbacks serve `index.html` and proxy `/api` on one port, that an unavailable
`[::1]` degrades to IPv4 with one warning, that a taken `[::1]` port fails the
start and frees the IPv4 port, and that `close()` releases both listeners.
After a Stable restart with the new server, the operator check is
`curl -s -o /dev/null -w '%{time_connect}' http://localhost:4011/` below 0.01 s
and `netstat -ano` showing both `127.0.0.1:4011` and `[::1]:4011` LISTENING.
