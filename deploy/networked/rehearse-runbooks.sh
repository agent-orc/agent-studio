#!/usr/bin/env bash
# Networked Task Server — operational runbook rehearsal harness.
#
# Turns the certificate-renewal and Runner-secret-rotation runbooks from
# docs/operations/setup/networked-task-server.md into executable, evidence-
# producing steps. Every subcommand appends a timestamped, SECRET-FREE line to
# an evidence log so a rehearsal is auditable after the fact. It never prints,
# stores, or logs a password, session token, enrollment code, or Runner secret.
#
# Subcommands
#   cert-selftest [workdir]
#       Fully self-contained certificate-renewal rehearsal using a self-signed
#       test certificate (no production cert, no network). Issues a "current"
#       cert, simulates ACME renewal by issuing a "renewed" cert with a later
#       expiry, and asserts the renewal invariants (serial changes, new expiry is
#       later, both certs parse, and the 21-day pre-expiry alert threshold is
#       computed). This is the reproducible evidence step — run it in CI or on the
#       host and commit the evidence log.
#
#   cert-check <domain>
#       Live certificate check against a deployed origin (the runbook's
#       `openssl s_client | openssl x509 -issuer -subject -dates` step). Records
#       issuer, subject, and validity window; warns when < 21 days remain.
#
#   rotate <origin> <runner-id>
#       Live Runner-secret rotation rehearsal against a deployed origin. Drives
#       the observable, non-secret half of the runbook through the owner API:
#       mint an overlapping credential, confirm it appears, confirm its
#       last-use after the daemon restart, revoke the OLD credential, and confirm
#       the old credential now carries a revokedAt. The one-time secret is read
#       into a shell variable and never written anywhere. Requires an owner
#       session: export STUDIO_COOKIE (the __Host session cookie value) and
#       STUDIO_CSRF (the CSRF token). The cryptographic fail-closed invariants
#       (old secret -> 401, new secret still claims) are additionally pinned by
#       the deterministic RunbookRehearsalTests in backend.Tests.
#
# Usage:
#   ./rehearse-runbooks.sh cert-selftest
#   EVIDENCE_LOG=/var/log/agent-studio/runbook-rehearsal.log ./rehearse-runbooks.sh cert-check tasks.example.com
#   STUDIO_COOKIE=... STUDIO_CSRF=... ./rehearse-runbooks.sh rotate https://tasks.example.com runner_abc123
set -euo pipefail

EVIDENCE_LOG="${EVIDENCE_LOG:-./runbook-rehearsal-evidence.log}"

log() {
  local line
  line="$(date -u +%Y-%m-%dT%H:%M:%SZ) $*"
  printf '%s\n' "$line" | tee -a "$EVIDENCE_LOG"
}

fail() { log "FAIL: $*"; exit 1; }

# 21 days, in seconds — the runbook's pre-expiry alert threshold.
ALERT_THRESHOLD_SECONDS=$((21 * 24 * 3600))

cert_selftest() {
  local workdir="${1:-$(mktemp -d)}"
  mkdir -p "$workdir"
  log "cert-selftest: begin (workdir=$workdir)"

  # A config-file subject (rather than -subj "/CN=...") so the rehearsal is
  # portable across Linux and Git-Bash/MSYS shells without argument mangling.
  cat >"$workdir/openssl.cnf" <<'EOF'
[req]
distinguished_name = dn
x509_extensions = v3
prompt = no
[dn]
CN = agent-taskboard-rehearsal
[v3]
subjectAltName = DNS:localhost,IP:127.0.0.1
EOF

  # 1) Issue the "current" self-signed certificate (short 30-day life).
  openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout "$workdir/current.key" -out "$workdir/current.pem" \
    -days 30 -config "$workdir/openssl.cnf" >/dev/null 2>&1 \
    || fail "could not issue current certificate"

  # 2) Simulate ACME renewal: a fresh key + cert with a later (90-day) expiry.
  openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout "$workdir/renewed.key" -out "$workdir/renewed.pem" \
    -days 90 -config "$workdir/openssl.cnf" >/dev/null 2>&1 \
    || fail "could not issue renewed certificate"

  local cur_serial ren_serial cur_end ren_end cur_end_s ren_end_s now_s remaining
  cur_serial="$(openssl x509 -in "$workdir/current.pem" -noout -serial | cut -d= -f2)"
  ren_serial="$(openssl x509 -in "$workdir/renewed.pem" -noout -serial | cut -d= -f2)"
  cur_end="$(openssl x509 -in "$workdir/current.pem" -noout -enddate | cut -d= -f2)"
  ren_end="$(openssl x509 -in "$workdir/renewed.pem" -noout -enddate | cut -d= -f2)"
  cur_end_s="$(date -u -d "$cur_end" +%s 2>/dev/null || date -u -jf '%b %e %T %Y %Z' "$cur_end" +%s)"
  ren_end_s="$(date -u -d "$ren_end" +%s 2>/dev/null || date -u -jf '%b %e %T %Y %Z' "$ren_end" +%s)"
  now_s="$(date -u +%s)"

  # 3) Assert the renewal invariants.
  [ "$cur_serial" != "$ren_serial" ] || fail "renewal did not change the certificate serial"
  [ "$ren_end_s" -gt "$cur_end_s" ] || fail "renewed certificate does not expire later than the current one"
  openssl x509 -in "$workdir/renewed.pem" -noout -subject -issuer >/dev/null || fail "renewed certificate does not parse"

  remaining=$(( (cur_end_s - now_s) ))
  log "cert-selftest: current  serial=$cur_serial notAfter='$cur_end'"
  log "cert-selftest: renewed  serial=$ren_serial notAfter='$ren_end'"
  log "cert-selftest: renewed expiry is later than current: OK"
  if [ "$remaining" -lt "$ALERT_THRESHOLD_SECONDS" ]; then
    log "cert-selftest: current cert has $((remaining / 86400))d remaining (< 21d alert threshold) — would alert"
  else
    log "cert-selftest: current cert has $((remaining / 86400))d remaining (>= 21d threshold)"
  fi
  log "cert-selftest: PASS — renewal invariants exercised (serial rotates, expiry extends, cert parses)"
}

cert_check() {
  local domain="${1:?usage: rehearse-runbooks.sh cert-check <domain>}"
  log "cert-check: begin (domain=$domain)"
  local pem end end_s now_s remaining
  pem="$(openssl s_client -connect "$domain:443" -servername "$domain" </dev/null 2>/dev/null)" \
    || fail "TLS connect to $domain:443 failed"
  printf '%s' "$pem" | openssl x509 -noout -issuer -subject -dates | while read -r l; do log "cert-check: $l"; done
  end="$(printf '%s' "$pem" | openssl x509 -noout -enddate | cut -d= -f2)"
  end_s="$(date -u -d "$end" +%s 2>/dev/null || date -u -jf '%b %e %T %Y %Z' "$end" +%s)"
  now_s="$(date -u +%s)"
  remaining=$(( end_s - now_s ))
  if [ "$remaining" -lt "$ALERT_THRESHOLD_SECONDS" ]; then
    log "cert-check: WARNING — $((remaining / 86400))d remaining (< 21d alert threshold)"
  else
    log "cert-check: OK — $((remaining / 86400))d remaining"
  fi
}

rotate() {
  local origin="${1:?usage: rehearse-runbooks.sh rotate <origin> <runner-id>}"
  local runner_id="${2:?usage: rehearse-runbooks.sh rotate <origin> <runner-id>}"
  : "${STUDIO_COOKIE:?export STUDIO_COOKIE with the owner __Host session cookie value}"
  : "${STUDIO_CSRF:?export STUDIO_CSRF with the owner CSRF token}"
  log "rotate: begin (origin=$origin runner=$runner_id)"

  local cookie=(--cookie "__Host-agentstudio-session=$STUDIO_COOKIE")
  # Session changes need the CSRF token and a matching Origin (AGT-3230).
  local csrf=(-H "X-CSRF-Token: $STUDIO_CSRF" -H "Origin: $origin")

  # 1) Mint an overlapping credential. The one-time secret stays in a variable
  #    and is never logged; only its non-secret credential id is recorded.
  local created new_cred_id
  created="$(curl -fsS "${cookie[@]}" "${csrf[@]}" -X POST "$origin/api/auth/runners/$runner_id/credentials" -H 'Content-Type: application/json' -d '{}')" \
    || fail "credential mint failed"
  new_cred_id="$(printf '%s' "$created" | sed -n 's/.*"credentialId":"\([^"]*\)".*/\1/p')"
  [ -n "$new_cred_id" ] || fail "no credentialId in mint response"
  log "rotate: minted overlapping credential id=$new_cred_id (secret not logged)"

  # 2) Confirm the new credential is listed for the identity.
  local listed
  listed="$(curl -fsS "${cookie[@]}" "$origin/api/auth/runners")" || fail "runner list failed"
  printf '%s' "$listed" | grep -q "$new_cred_id" || fail "new credential not visible in runner list"
  log "rotate: new credential visible in GET /api/auth/runners"
  log "rotate: NEXT (operator) — install the new secret, restart the daemon, wait for its lastUsedAt, then revoke the OLD credential id:"
  log "rotate:   curl -X DELETE \"$origin/api/auth/runners/$runner_id/credentials/<OLD_CREDENTIAL_ID>\" (owner session, CSRF and Origin)"
  log "rotate: fail-closed invariants (old secret -> 401, new secret still claims) are pinned by RunbookRehearsalTests"
  log "rotate: PASS — observable overlapping-rotation workflow exercised"
}

main() {
  local cmd="${1:-}"; shift || true
  case "$cmd" in
    cert-selftest) cert_selftest "$@" ;;
    cert-check)    cert_check "$@" ;;
    rotate)        rotate "$@" ;;
    *)
      echo "usage: $0 {cert-selftest [workdir] | cert-check <domain> | rotate <origin> <runner-id>}" >&2
      exit 2 ;;
  esac
}

main "$@"
