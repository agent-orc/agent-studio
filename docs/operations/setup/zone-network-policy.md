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

The trade-off is that the rules match addresses, not names. Host names are
resolved when the rules are applied. The hourly timer reapplies them so the
addresses stay current. A destination behind a CDN whose addresses change
faster than that can be listed by CIDR instead (for example GitHub's published
ranges).

## Files

| File | Role |
|---|---|
| [deploy/zones/zone-policy.json](../../../deploy/zones/zone-policy.json) | The policy: destinations, zones, networks, verification probes. Raise `version` with every change. |
| [deploy/zones/apply-zone-policy.sh](../../../deploy/zones/apply-zone-policy.sh) | Operator entry point. Dry run by default; `--execute` applies, verifies, and records the result. |
| [deploy/systemd/agent-zone-policy.service](../../../deploy/systemd/agent-zone-policy.service), [`.timer`](../../../deploy/systemd/agent-zone-policy.timer) | Reapply after boot and every hour. |
| [deploy/zones/compose.work-zone.yaml](../../../deploy/zones/compose.work-zone.yaml) | Compose overlay that moves the agent-host containers of a work host onto `agent-work`. |
| `agent-host zone-policy render\|apply\|verify` | The renderer, applier and verifier (`runner/ZonePolicy*.cs`). |
| `/var/lib/agent-studio/zone-policy/applied.json` | Applied state: policy version and digest, rules digest, zones, last verification. |

## Network model

| Zone | Network | Inbound | Egress |
|---|---|---|---|
| work | `agent-work`, bridge `br-agent-work`, `172.30.0.0/24`, inter-container traffic off | none: no published ports, no connection into the bridge | allowlist only: DNS resolvers, control plane, Git hosts, package registries, model provider APIs; everything else is rejected |
| run | one network per tenant (`agent-run-<tenant>`, bridge `br-run-<tenant>`) | not changed by this cut; the TLS proxy of the run-zone host agent publishes apps (IP-1, AGT-3229) | open, except every `control-plane` and `secret-store` destination; no path to another tenant, to a work bridge, or to host services other than DNS |

Validation enforces the Dossier's fixed boundaries. A work zone cannot allow a
`secret-store` destination. A run zone must deny every `control-plane` and
`secret-store` destination. Every run network names its own tenant. Subnets
and bridge names must not overlap. Each zone needs at least one reachable and
one blocked probe. Addresses are IPv4 only, because the zone networks are
created without IPv6.

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
  address.

Both chains first return established and related traffic. Rejections use
`icmp-admin-prohibited`, so a blocked tool fails at once instead of timing out.
The golden files in `runner.Tests/Fixtures/zone-policy/` show the full rendered
rules and plan for the checked-in policy.

## Install on a Docker host

1. Copy the policy and the script, and create the environment file:

   ```sh
   sudo install -d -m 0755 /etc/agent-studio/zone-policy /var/lib/agent-studio/zone-policy
   sudo install -m 0644 deploy/zones/zone-policy.json /etc/agent-studio/zone-policy/zone-policy.json
   sudo install -m 0755 deploy/zones/apply-zone-policy.sh /usr/local/libexec/agent-apply-zone-policy
   sudo tee /etc/agent-studio/zone-policy/zone-policy.env >/dev/null <<'EOF'
   # Upstream resolvers Docker's embedded DNS forwards to (see /run/systemd/resolve/resolv.conf).
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

   It prints the numbered plan, the full rules, and one line per step:
   `unchanged`, `would-create`, `would-apply` (the rules passed
   `iptables-restore --test`) or `would-insert`. Run without root, the rule
   checks read `unchecked`. A `conflict` means a zone network already exists
   with other settings. The script never recreates such a network under
   running containers. Detach the containers and remove the network, or change
   the policy.

3. Apply and enable the timer:

   ```sh
   sudo install -m 0644 deploy/systemd/agent-zone-policy.service deploy/systemd/agent-zone-policy.timer /etc/systemd/system/
   sudo systemctl daemon-reload
   sudo systemctl start agent-zone-policy.service   # same as: agent-apply-zone-policy --execute
   sudo systemctl enable --now agent-zone-policy.timer
   ```

4. Attach the work containers. For the Compose agent hosts:

   ```sh
   RUNNER_SERVER_URL=https://task-server.example.internal \
     docker compose -f docker-compose.yml -f deploy/zones/compose.work-zone.yaml \
     up -d --no-deps agent-host-distributed agent-host-review-distributed
   ```

   Other work containers join with `--network agent-work` and no `-p`. Mount
   `/var/lib/agent-studio/zone-policy` read-only into each daemon container.
   A native daemon reads the default path directly (`RUNNER_ZONE_POLICY_STATE`
   overrides it).

## Verification

`--execute` ends with the verification. `--verify` repeats it at any time, and
`--verify --record` also updates the recorded result. For each zone network,
one throw-away container (`bash:5.2`, label `org.agent-studio.zone-probe=true`)
opens each probe connection with bash's `/dev/tcp` under `timeout`:

- A **reachable** probe connects by name, so the resolver allowance is tested
  too. It passes only on a completed connection.
- A **blocked** probe connects to every resolved address of the destination,
  so a DNS failure cannot pass for a block. It passes on a refusal or a timeout.
- **No inbound** passes when no container on a work network publishes a port.
- A probe container that cannot start, or a name that does not resolve, is
  inconclusive. It fails the verification.

Exit codes: `0` passed, `1` verification failed, `2` usage, policy or apply
error. A failed apply or verification is recorded too. The daemons then report
`zone-policy` as `unavailable`, and the host reads as degraded. If the rendered
rules differ from the applied ones because the policy or DNS changed,
`--verify --record` refuses and asks for `--execute`.

## Capability and Environment view

Every coding, review and gate daemon adds this entry to its capability
advertisement:

| Field | Value |
|---|---|
| key | `zone-policy` (category `zone`) |
| status | `ready` after a passed verification, `unavailable` after a failed one or for an unreadable record |
| version | the policy `version` |
| identity | the work zone's name |
| detail | `verifiedAt=…; networks=…; rules=sha256:<12>` or the first failed check |

The capability is informational and never a claim requirement. A host without
a record (for example a Windows workstation) reports no entry. The Environment
view shows each host's zone policy: the zone and version when verified,
`verification failed` with the reason otherwise, and `Not reported` when
there is no entry.

## Limits of this cut

- A one-box install keeps the work and control zones on one host and one
  Compose control network. The Dossier's D1 separation needs a dedicated work
  host.
- Containers that a gate's Compose scenario starts get their own Compose
  networks and are not covered by the work zone rules.
- Run-zone inbound (TLS proxy, domains) and app-declared egress belong to the
  run-zone host agent (IP-1, AGT-3229). This cut only guarantees the denials.
