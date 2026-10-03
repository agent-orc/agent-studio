#!/bin/sh
# Host-side connectivity proof for one runner host, run ON the runner host as the
# runner service user. Reads the installation connectivity manifest, derives the
# URL this runner must use, and proves: health, an authenticated request, TLS trust
# and certificate expiry (private-https), and exactly one loopback listener on the
# tunnel port (reverse-ssh). Never prints the credential.
#
# Usage: probe-runner-route.sh <manifest.json> <runner-id> <credential-file>
set -eu

manifest=${1:?manifest path}
runner=${2:?runner id}
credential_file=${3:?credential file (owner-only)}
warn_days=${CERT_WARN_DAYS:-14}

fail() { printf 'FAIL %s: %s\n  remediation: %s\n' "$1" "$2" "$3" >&2; exit 1; }
ok() { printf 'OK   %s\n' "$*"; }

command -v jq >/dev/null || fail tooling "jq is required" "Install jq on the runner host."
[ -r "$credential_file" ] || fail credential "cannot read $credential_file" "Provision the runner credential over the administration channel (chmod 600)."
case "$(stat -c %a "$credential_file")" in 600|400) ;; *) fail credential "credential file is not owner-only" "chmod 600 $credential_file";; esac

route=$(jq -r --arg r "$runner" '.runners[] | select(.runnerId==$r) | .route' "$manifest")
[ -n "$route" ] || fail manifest "runner $runner has no route" "Add exactly one runners[] entry for $runner and bump revision."
origin=$(jq -r '.serverOrigin' "$manifest")
ca=$(jq -r '.caBundleFile // empty' "$manifest")
curl_tls=""

case "$route" in
  reverse-ssh)
    port=$(jq -r --arg r "$runner" '.runners[] | select(.runnerId==$r) | (.remotePort // 15031)' "$manifest")
    url="http://127.0.0.1:$port"
    rows=$(ss -Htln "sport = :$port" | awk '{print $4}')
    count=$(printf '%s\n' "$rows" | grep -c . || true)
    [ "$count" -ge 1 ] || fail dropped-tunnel "no listener on 127.0.0.1:$port" \
      "Check the link owner: GET /api/v1/management/links on the supervisor; POST .../links/$runner/reconnect. Do not start a second keeper."
    printf '%s\n' "$rows" | grep -qvE '^(127\.0\.0\.1|\[::1\]):' && fail exposed-tunnel "tunnel port bound beyond loopback: $rows" \
      "Set GatewayPorts no in the runner sshd_config and reconnect the link."
    [ "$count" -le 2 ] || fail duplicated-listener "$count listeners on $port" \
      "Two owners hold the route. Stop the owner not named in linkOwner (TunnelKeeper scheduled task or LinkSupervisor)."
    ok "one loopback tunnel listener on $port" ;;
  private-https)
    url=$origin
    [ -r "$ca" ] || fail private-trust "CA bundle $ca missing" "Copy the private CA certificate to $ca."
    curl_tls="--cacert $ca"
    host=$(printf '%s' "$origin" | sed -E 's#^https://([^/:]+).*#\1#')
    getent hosts "$host" >/dev/null || fail private-dns "$host does not resolve" "Set the WireGuard DNS (wg0 DNS=) or /etc/hosts entry for $host; check wg show."
    end=$(echo | openssl s_client -connect "$host:443" -servername "$host" -CAfile "$ca" 2>/dev/null | openssl x509 -noout -enddate | cut -d= -f2)
    [ -n "$end" ] || fail tls-handshake "no certificate from $host:443" "Check wg show handshake, edge container health and firewall on wg0."
    left=$(( ($(date -d "$end" +%s) - $(date +%s)) / 86400 ))
    [ "$left" -ge 0 ] || fail tls-expired "certificate expired $end" "Renew the edge certificate, restart the edge, rerun this probe. Never pass --insecure."
    [ "$left" -ge "$warn_days" ] || printf 'WARN tls-expiring: %s days left\n' "$left"
    ok "TLS chain verified against $ca, $left days left" ;;
  loopback) url=$origin ;;
  *) fail manifest "unknown route $route" "Use loopback, reverse-ssh or private-https." ;;
esac

# shellcheck disable=SC2086
curl --fail --silent --show-error --max-time 5 $curl_tls "$url/healthz" >/dev/null \
  || fail health "$url/healthz did not answer" "See the failure table in docs/operations/setup/connectivity-manifest.md."
ok "health $url/healthz"
# Authentication proof without mutation: anonymous must be refused, the scoped credential
# must pass the auth layer (a 404/405 from the route after auth is acceptable).
probe_path="$url/api/v1/runners/$runner"
# shellcheck disable=SC2086
anon=$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 5 $curl_tls "$probe_path")
[ "$anon" = 401 ] || fail auth-open "anonymous request returned $anon, expected 401" \
  "The server is not enforcing bearer auth on this route; set AUTH=bearer before admitting runners."
# shellcheck disable=SC2086
code=$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 5 $curl_tls \
  -H "Authorization: Bearer $(cat "$credential_file")" "$probe_path")
case "$code" in
  401|403) fail auth "credential rejected ($code)" "Re-enrol $runner and reprovision its scoped credential; never reuse another host's secret." ;;
  000|5*) fail auth "authenticated request failed ($code)" "Check that the runner server URL equals the URL above for this route." ;;
  *) ok "anonymous 401, scoped credential accepted by auth ($code)" ;;
esac
printf 'connectivity revision %s route %s url %s\n' "$(jq -r .revision "$manifest")" "$route" "$url"
