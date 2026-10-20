# Zone network policy on Docker hosts

AGT-3231 implements card IP-2 of the
[integrated platform Dossier](../integrierte-plattform/index.html) (§4 and §5)
for Docker hosts. Work containers (coding, review, gate) get no inbound path,
and their egress is limited to an allowlist. Run-zone app containers never
reach the control plane or the secret store, and each tenant gets its own
network. One policy file is checked in. A host-side command renders it into
iptables rules, applies them, and proves them with probe containers. The
daemons then report the applied policy version as their `zone-policy`
capability, and the Environment view shows it for each host.

## Implemented option

The card allowed an egress proxy or iptables rules generated from the policy.
This change uses **iptables rules**, for these reasons:

- Git over SSH and CLIs that ignore `HTTPS_PROXY` keep working without a
  proxy configuration in every container.
- The rule set is enforced on the host, below the containers. A container
  cannot opt out by unsetting a proxy variable.
- The verification measures the same thing the rules enforce: TCP reachability
  from a container on the zone network.

Rules match addresses, not names, and the Git hosts, package registries and
model APIs sit behind CDNs that hand out different addresses from one lookup to
the next. A snapshot taken at apply time would reject traffic whenever a
container resolved to another address. So the allowed set follows what the
containers resolve:

- A host-side **zone resolver** (dnsmasq, unit `agent-zone-dns.service`)
  listens on the bridge gateway of every work network. Work containers use it
  as their only resolver (`--dns 172.30.0.1`). The work zone may not reach any
  other resolver.
- The zone resolver forwards only the allowed names and their subdomains, one
  `server=/<name>/<upstream>` line per name and upstream. It has no general
  `server=` line, so every other name gets `REFUSED`. The resolver is no DNS
  channel out of the work zone.
- For each allowed destination with host names, apply creates the ipset
  `agent-zone-<destination>` (`hash:ip`, `timeout 86400`). dnsmasq adds every
  IPv4 answer for one of those names, or for a subdomain of one, to that set
  before it answers the container. Each new answer refreshes the entry's
  timeout. An address that nobody resolved for `resolver.setTimeoutSeconds`
  expires. dnsmasq adds to a set only on an upstream reply, not on a cache
  hit. So the configuration sets `max-cache-ttl` to half the set timeout
  (43200 s): an address served from the cache was added less than half a
  timeout ago. dnsmasq writes its own pid file, `/run/agent-zone-dns.pid`.
- The work rules match a named destination with
  `-m set --match-set agent-zone-<destination> dst` and its ports. IPv4
  addresses and CIDRs in the policy stay literal `-d` rules.

An address stays allowed after its DNS TTL ran out, so a container that reuses
an older answer still connects. A name is resolved on the host only where an
address must be known up front: for the run zone's denials and for blocked
probes. Those are internal addresses that the timer re-resolves.

## Files

| File | Role |
|---|---|
| [deploy/zones/zone-policy.json](../../../deploy/zones/zone-policy.json) | The policy: destinations, zones, networks, verification probes. Raise `version` with every change. |
| [deploy/zones/apply-zone-policy.sh](../../../deploy/zones/apply-zone-policy.sh) | Operator entry point. Dry run by default; `--execute` applies, verifies, and records the result. `--remove` rolls the rules back. |
| [deploy/systemd/agent-zone-policy.service](../../../deploy/systemd/agent-zone-policy.service), [`.timer`](../../../deploy/systemd/agent-zone-policy.timer) | Apply at boot (`WantedBy=multi-user.target`) and reapply and verify every 15 minutes. |
| [deploy/systemd/agent-zone-dns.service](../../../deploy/systemd/agent-zone-dns.service) | The zone resolver (dnsmasq). Apply writes its configuration and starts or restarts it. |
| [deploy/zones/compose.work-zone.yaml](../../../deploy/zones/compose.work-zone.yaml) | Compose overlay that moves the agent-host containers of a work host onto `agent-work` with the zone resolver as DNS. |
| `agent-host zone-policy render\|apply\|verify\|remove` | The renderer, applier, verifier and rollback (`runner/ZonePolicy*.cs`). |
| `/var/lib/agent-studio/zone-policy/applied.json` | Applied state: policy version and digest, rules digest, zones, last verification, kernel boot id, removal time. |
| `/var/lib/agent-studio/zone-policy/dnsmasq.conf` | Rendered zone resolver configuration. |

## Network model

| Zone | Network | Inbound | Egress |
|---|---|---|---|
| work | `agent-work`, bridge `br-agent-work`, `172.30.0.0/24`, gateway `172.30.0.1`, inter-container traffic off, IPv6 off | none: no published ports, no connection into the bridge | allowlist only: the zone resolver on the gateway, control plane, Git hosts, package registries, model provider APIs; everything else is rejected |
| run | one network per tenant (`agent-run-<tenant>`, bridge `br-run-<tenant>`), IPv6 off | not changed by this cut; the TLS proxy of the run-zone host agent publishes apps (IP-1, AGT-3229) | open, except every `control-plane` and `secret-store` destination, also through a published port; no path to another tenant, a work bridge, a Compose network (`br-*`), `docker0`, or host services other than DNS |

Validation enforces the Dossier's fixed boundaries. A work zone cannot allow a
`secret-store` destination. A run zone must deny every `control-plane` and
`secret-store` destination. Every run network names its own tenant. Subnets
and bridge names must not overlap. Each zone needs at least one reachable and
one blocked probe. Addresses are IPv4 only, because the zone networks are
created with `--ipv6=false`; an existing zone network with IPv6 enabled, or
with another subnet or gateway, is a conflict. Subnets must be written in
canonical form (`172.30.0.0/24`, not `172.30.0.0/024`). Zone, network, tenant
and destination names must match `^[a-z0-9][a-z0-9-]{0,47}$`, because they
appear verbatim in the rule comments. The policy `version` must match
`^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$`, because it appears verbatim in the header
lines of both payloads and of the resolver configuration; a line break there
would add lines to the `iptables-restore --noflush` input. An allowed
destination's name is at most 20 characters, so its ipset name fits the
kernel's 31-character limit. Host entries are IPv4 addresses, canonical CIDRs
(rendered as `addr/prefix`) or DNS names. The probe image must be pinned by
digest and must not start with `-`.

### Rules

The rendered payload is loaded with `iptables-restore --noflush`. It declares
only two chains, so a load atomically replaces those two chains and nothing
else:

- `AGENT-ZONE-FWD`, jumped to from the top of `DOCKER-USER`, covers traffic
  that leaves or enters a zone bridge. Docker evaluates `DOCKER-USER` before
  its own rules.
- `AGENT-ZONE-IN`, jumped to from the top of `INPUT`, covers container traffic
  to the host itself, including the bridge gateway address. Without it, a
  container could reach a service on the same host through the gateway
  address. The work zone may use only port 53 of its gateway (the zone
  resolver) there.

Both chains first return established and related traffic. Rejections use
`icmp-admin-prohibited`, so a blocked tool fails at once ("No route to host")
instead of timing out.

The run zone's denials in `AGENT-ZONE-FWD` match `-m conntrack --ctorigdst`.
FORWARD sees a connection after Docker's port forwarding has rewritten its
destination. Without `--ctorigdst`, an app that dials a port the control plane
publishes on the host would get through. After the denials, a run network may
reach its own bridge. Every other `br-+` bridge and `docker0` is dropped.

An IPv6 payload, loaded with `ip6tables-restore --noflush`, declares the same
two chains and drops every packet on a zone bridge, in and out. Its
`AGENT-ZONE-FWD` hangs off the IPv6 `DOCKER-USER`, or off `FORWARD` when
Docker runs without ip6tables support and that chain does not exist. Its
`AGENT-ZONE-IN` hangs off the IPv6 `INPUT`. On a kernel without IPv6 (no
`/proc/net/if_inet6`, or `ip6tables` reports that the address family is not
supported), apply and remove report the IPv6 payload and its jumps as
`skipped`. There is no IPv6 traffic to drop on such a host. A kernel with
`/proc/net/if_inet6` but without the ip6tables filter table ("Table does not
exist") does carry IPv6, so there the IPv6 step fails: load `ip6table_filter`
and apply again.

### Apply order

Apply works in this order: Docker networks, ipsets, the IPv4 payload and its
jumps, the IPv6 payload and its jumps, then the zone resolver. The rules are
in place before the resolver starts. Until it answers, the sets are empty and
every named destination is denied. A resolver that fails to start fails the
apply, and the failure is recorded, but the rules stay loaded. A network or
ipset conflict stops the apply before any rule loads.

A name is resolved on the host only for a run-zone denial and for a blocked
probe. If a denied name does not resolve, apply still loads every other rule,
but that denial is missing. The verification then records the state as failed
(`setup deny <destination> expected resolved, observed unresolved`). If a
blocked probe's host does not resolve, that blocked check fails during the
verification. Neither stops the rules from loading.

The golden files in `runner.Tests/Fixtures/zone-policy/` show the full IPv4
and IPv6 rules, the resolver configuration and the plan for the checked-in
policy.

## Install on a Docker host

1. Install the tools, copy the policy, the script and the units, and create
   the environment file. `dnsmasq-base` ships the dnsmasq binary without the
   distribution's own `dnsmasq.service`, which would claim port 53 on every
   interface:

   ```sh
   sudo apt-get install -y ipset dnsmasq-base
   sudo install -d -m 0755 /etc/agent-studio/zone-policy /var/lib/agent-studio/zone-policy
   sudo install -m 0644 deploy/zones/zone-policy.json /etc/agent-studio/zone-policy/zone-policy.json
   sudo install -m 0755 deploy/zones/apply-zone-policy.sh /usr/local/libexec/agent-apply-zone-policy
   sudo install -m 0644 deploy/systemd/agent-zone-policy.service deploy/systemd/agent-zone-policy.timer \
     deploy/systemd/agent-zone-dns.service /etc/systemd/system/
   sudo systemctl daemon-reload
   sudo tee /etc/agent-studio/zone-policy/zone-policy.env >/dev/null <<'EOF'
   # Upstream resolvers the zone resolver forwards to (IPv4 addresses, see
   # /run/systemd/resolve/resolv.conf). They must resolve the control-plane host.
   AGENT_ZONE_DNS_RESOLVERS=192.0.2.53
   # Host of RUNNER_SERVER_URL and the Studio edge.
   AGENT_ZONE_CONTROL_PLANE_HOSTS=task-server.example.internal
   # Secret provider and credential manager addresses (run hosts only).
   AGENT_ZONE_SECRET_STORE_HOSTS=192.0.2.80
   # A work host applies only its zone; a run host sets AGENT_ZONES=run.
   AGENT_ZONES=work
   EOF
   ```

   The script expects the agent-host binary at `/opt/agent-host/current/agent-host`
   (the managed Linux install). On a host that runs the daemons only as
   containers, install the release's self-contained `agent-host` binary for
   this tooling and set `AGENT_HOST_BIN`.

2. Run the dry run and read the plan:

   ```sh
   ZONE_POLICY_FILE=/etc/agent-studio/zone-policy/zone-policy.json \
     /usr/local/libexec/agent-apply-zone-policy
   ```

   It prints the numbered plan, the full rules and the resolver configuration,
   and one line per step: `unchanged`, `would-create`, `would-write`,
   `would-start`, `would-apply` (the rules passed `iptables-restore --test`)
   or `would-insert`. Run without root, the ipset and rule checks read
   `unchecked`. On a fresh host the IPv4 rule check also reads `unchecked`,
   because the kernel checks set matches only against existing sets. On a
   kernel without IPv6, the IPv6 steps read `skipped`. A `conflict` means a
   zone network or ipset already exists with other settings. The script never
   recreates such a network under running containers. Detach the containers
   and remove the network, or change the policy. For an ipset, roll back (see
   below), run `ipset destroy <set>`, and apply again.

3. Apply, then enable the service for boot and the timer:

   ```sh
   sudo systemctl start agent-zone-policy.service   # same as: agent-apply-zone-policy --execute
   sudo systemctl enable agent-zone-policy.service
   sudo systemctl enable --now agent-zone-policy.timer
   ```

   The rules and ipsets do not survive a reboot. The enabled service applies
   them again at boot. It starts after `docker.service` and
   `network-online.target`. Containers that Docker restarts on its own run
   without the zone rules until the IPv4 payload and its jumps are loaded.
   That window covers the wait for `network-online.target` (up to about two
   minutes with `systemd-networkd-wait-online`) plus the network and ipset
   steps. It is not just a few seconds. The resolver starts last, so a work
   container cannot resolve anything until then. If DNS is not up yet at boot,
   a denied name may not resolve: the other rules still load, the state reads
   failed, and the timer's next run within 15 minutes completes it.

4. Attach the work containers. For the Compose agent hosts:

   ```sh
   RUNNER_SERVER_URL=https://task-server.example.internal \
     docker compose -f docker-compose.yml -f deploy/zones/compose.work-zone.yaml \
     up -d --no-deps agent-host-distributed agent-host-review-distributed
   ```

   Other work containers join with `--network agent-work --dns 172.30.0.1`
   and no `-p`. Without `--dns`, a container asks an upstream resolver
   directly. That request is rejected, and the container cannot resolve any
   name. Mount `/var/lib/agent-studio/zone-policy` read-only into each daemon
   container and set
   `RUNNER_ZONE_POLICY_STATE=/var/lib/agent-studio/zone-policy/applied.json`.
   The overlay does both. A native daemon reports the zone policy only with
   that variable in its environment file. There is no default path.

## Verification

`--execute` ends with the verification. `--verify` repeats it at any time, and
`--verify --record` also updates the recorded result. The mutating runs
(`--execute`, `--verify --record` and `--remove --execute`) run as root and
hold an exclusive `flock` on `/run/agent-zone-policy.lock` (set
`ZONE_POLICY_LOCK` to change it), so the timer's apply and an operator's run
never interleave. A second mutating run waits up to 600 seconds
(`ZONE_POLICY_LOCK_WAIT`) and then gives up with exit code 2. The lock lives in
root-owned `/run`, not in the world-writable `/run/lock`: there, any local user
could create or hold the file, and with `fs.protected_regular` (the Ubuntu
default) root could no longer open a file another user created. The script
refuses a lock file that is a symbolic link or belongs to another user. Dry
runs, a plain `--verify` and `--render` take no lock, so a non-root dry run
never blocks a later root apply. For each zone
network, one throw-away container (`bash:5.2` pinned by digest, label
`org.agent-studio.zone-probe=true`) opens each probe connection with bash's
`/dev/tcp` under `timeout`. On a work network the container uses the zone
resolver (`--dns <gateway>`), as the work containers do:

- A **reachable** probe connects by name. That tests the zone resolver and the
  set it fills as well. It passes only on a completed connection.
- A **blocked** probe connects to every resolved address of the destination,
  on every port the destination lists unless the probe names one, so a DNS
  failure cannot pass for a block. It passes only on "No route to host" (the
  zone's `icmp-admin-prohibited` reject) or a timeout (a drop). "Connection
  refused" is a reset from the real destination. Nothing blocked that
  connection, so the check fails, like any other connect error.
- A **control** probe runs once per blocked target from
  `verification.controlNetwork` (Docker's default `bridge`, outside every
  zone). It passes when the target answers there, with a connection or a
  reset. A timeout or reject means the target is unreachable anyway, so the
  block proves nothing, and the check fails.
- **No inbound** passes when no container on a work network publishes a port.
- A probe container that cannot start, or a name that does not resolve, is
  inconclusive. It fails the verification. In a work network, a name the zone
  resolver refuses (one the work zone does not allow) also reads
  `unresolved`.

Exit codes: `0` passed, `1` verification failed, `2` usage, policy, name
resolution or apply error. Under `--execute`, a failed apply or verification is
recorded. So is a policy, environment or name resolution error, and any other
error, such as an unreadable file. The daemons then report
`zone-policy` as `unavailable`, and the host reads as degraded. If the rendered
rules differ from the applied ones because the policy or a denied address
changed, `--verify --record` refuses and asks for `--execute`.

## Capability and Environment view

Every coding, review and gate daemon adds this entry to its capability
advertisement:

| Field | Value |
|---|---|
| key | `zone-policy` (category `zone`) |
| status | `ready` after a passed verification of this boot within the last 30 minutes; otherwise `unavailable` |
| version | the policy `version` |
| identity | the work zone's name |
| detail | `verifiedAt=…; networks=…; rules=sha256:<12>`, or why the record does not count |

The record does not count, and the status is `unavailable`, when:

- the verification failed (the detail names the first failed check);
- the record carries another kernel boot id than the host
  (`/proc/sys/kernel/random/boot_id`, which a container shares with its host),
  because a reboot dropped the rules and ipsets;
- the last verification is older than twice the timer interval (30 minutes),
  because the timer stopped;
- `zone-policy remove --execute` removed the rules, or stopped partway (the
  detail names the failed step);
- the record is unreadable or from the first cut (schema 1, without a boot id).

The capability is informational and never a claim requirement. A daemon
reports it only when `RUNNER_ZONE_POLICY_STATE` is set. A daemon without the
variable, or without the file, reports no entry. The Environment view shows each
host's zone policy: the zone and version when verified,
`verification failed` with the reason after a failed check, `not verified`
with the reason for any other `unavailable` record, and `Not reported` when
there is no entry.

## Rollback

`--remove` takes the zone rules off the host. Like apply, it is a dry run
unless `--execute` is given, and a second run changes nothing:

1. Stop the timer and the boot unit, so nothing reapplies the rules:

   ```sh
   sudo systemctl disable --now agent-zone-policy.timer agent-zone-policy.service
   ```

2. Read the plan, then remove:

   ```sh
   /usr/local/libexec/agent-apply-zone-policy --remove
   sudo /usr/local/libexec/agent-apply-zone-policy --remove --execute
   ```

   It deletes the jumps from `DOCKER-USER` and `INPUT` (IPv4 and IPv6, plus
   the IPv6 `FORWARD` fallback), then the `AGENT-ZONE-FWD` and
   `AGENT-ZONE-IN` chains of both families. It also marks the applied state as
   removed, so the daemons report `zone-policy` as `unavailable`. Unset
   `RUNNER_ZONE_POLICY_STATE` to stop reporting it. If a step fails, the
   state is marked as an incomplete removal with the failed step. The daemons
   report `unavailable`, because some rules may be gone already. Fix the
   cause and run `--remove --execute` again. It skips what is gone and
   completes the removal. On a kernel without IPv6, the IPv6 steps read
   `skipped`.

3. Docker networks, ipsets and the zone resolver stay, because work
   containers may still use them (`--dns 172.30.0.1`). Once the containers
   run without the zone (the Compose services without
   `compose.work-zone.yaml`), remove them too:

   ```sh
   sudo systemctl stop agent-zone-dns.service
   for set in $(sudo ipset list -n | grep '^agent-zone-'); do sudo ipset destroy "$set"; done
   docker network rm agent-work agent-run-default
   sudo rm -f /var/lib/agent-studio/zone-policy/dnsmasq.conf
   ```

To return to the zone policy, enable the service and timer again (install
step 3). The first apply recreates everything.

## Limits of this cut

- A one-box install keeps the work and control zones on one host and one
  Compose control network. The Dossier's D1 separation needs a dedicated work
  host.
- Containers that a gate's Compose scenario starts get their own Compose
  networks and are not covered by the work zone rules.
- Run-zone inbound (TLS proxy, domains) and app-declared egress belong to the
  run-zone host agent (IP-1, AGT-3229). This cut only guarantees the denials.
- A name the work zone allows also allows its subdomains (dnsmasq's `ipset=`
  and `server=` semantics). A container that connects to a resolved address
  without asking the zone resolver first is rejected. That is intended.
- The allowlist works on layer 4: it matches addresses and ports, not TLS
  server names or HTTP hosts. Sites that share a CDN address cannot be told
  apart. Once an allowed name resolves to a shared address (Fastly,
  Cloudflare, Google front ends), every other site on that address is
  reachable from the work zone on the allowed ports until the set entry
  expires.
- The same holds for the run-zone denials. A control plane fronted by
  Cloudflare or another shared CDN resolves to addresses that serve many
  unrelated sites. Denying them blocks those sites for the apps, and it is no
  boundary: the control plane may answer on other CDN addresses. List the
  control plane's literal addresses (its origin or a private address) in
  `AGENT_ZONE_CONTROL_PLANE_HOSTS` instead of a CDN name.
- A host `INPUT` firewall such as ufw must accept UDP and TCP port 53 from
  `br-agent-work` to the work gateway, for example
  `sudo ufw allow in on br-agent-work to 172.30.0.1 port 53`. `AGENT-ZONE-IN`
  only returns that traffic to `INPUT`; a later default deny there would
  still drop it, and work containers could not resolve any name.
- Hosts on the allowlist reflect the CLIs in use. Gemini CLI's Google sign-in
  uses `cloudcode-pa.googleapis.com`, and GitHub release downloads redirect to
  `release-assets.githubusercontent.com`; both are listed since policy
  `2026-10-20.3`.
