#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 6 ]; then
  echo 'Usage: restore-to-scratch.sh <backup.cms> <certificate> <private-key> <empty-scratch-directory> <stack-directory> <signal.json>' >&2
  exit 2
fi
archive="$(realpath "$1")"
certificate="$(realpath "$2")"
private_key="$(realpath "$3")"
scratch="$(realpath -m "$4")"
stack="$(realpath "$5")"
signal="$(realpath -m "$6")"
[ ! -e "$scratch" ] || [ -z "$(ls -A "$scratch")" ] || { echo 'Scratch directory must be empty' >&2; exit 2; }
case "$private_key" in "$scratch"/*|"$stack"/*) echo 'Decryption key must be kept separately from the installation' >&2; exit 2 ;; esac
case "$signal" in "$scratch"/*) echo 'Monitoring signal must be outside the scratch installation' >&2; exit 2 ;; esac
umask 077
mkdir -p "$scratch/data" "$scratch/stack" "$(dirname "$signal")"
result=fail
reason=restore-incomplete
project="studio-restore-$$"
cleanup() {
  code=$?
  [ -z "${probe:-}" ] || rm -rf "$probe"
  if [ "${started:-false}" = true ]; then
    docker compose --project-name "$project" --project-directory "$scratch/stack" \
      --env-file "$scratch/stack/.env" -f "$scratch/stack/compose.yaml" down > /dev/null 2>&1 || true
  fi
  python3 - "$signal" "$result" "$reason" "$archive" <<'PY'
import datetime, hashlib, json, pathlib, sys
signal, result, reason, archive = sys.argv[1:]
digest = hashlib.sha256(pathlib.Path(archive).read_bytes()).hexdigest()
pathlib.Path(signal).write_text(json.dumps({
    'signal': 'website.restore.signin', 'status': result, 'reason': reason,
    'observedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'backupSha256': digest,
}) + '\n')
PY
  exit "$code"
}
trap cleanup EXIT

openssl cms -decrypt -binary -inform DER -in "$archive" -recip "$certificate" \
  -inkey "$private_key" > "$scratch/backup.tar" || { reason=decrypt-failed; rm -f "$scratch/backup.tar"; exit 1; }
python3 - "$scratch/backup.tar" "$scratch/data" <<'PY'
import pathlib, sys, tarfile
root = pathlib.Path(sys.argv[2])
with tarfile.open(sys.argv[1]) as archive:
    archive.extractall(root, filter='data')
PY
rm -f "$scratch/backup.tar"
mv "$scratch/data/.env" "$scratch/stack/.env"
cp "$stack"/compose.yaml "$stack"/Caddyfile.* "$stack"/cloudflare-ips.caddy "$scratch/stack/"
python3 - "$scratch" <<'PY'
import pathlib, sys
root = pathlib.Path(sys.argv[1])
env = root / 'stack/.env'
values = {}
for line in env.read_text().splitlines():
    key, sep, value = line.partition('=')
    if sep:
        values[key] = value
values.update(STUDIO_DATA_DIR=str(root / 'data'), STUDIO_DOMAIN='restore.localhost',
              STUDIO_CADDYFILE='./Caddyfile.private', STUDIO_PUBLIC_CLOUDFLARE_EDGE='false',
              STUDIO_RESTART_POLICY='unless-stopped')
env.write_text(''.join(f'{key}={value}\n' for key, value in values.items()))
compose = root / 'stack/compose.yaml'
text = compose.read_text().replace("'0.0.0.0:80:80'", "'127.0.0.1::80'")
text = text.replace("'0.0.0.0:443:443'", "'127.0.0.1::443'")
compose.write_text(text)
PY
reason=signin-probe-failed
if [ "${STUDIO_RESTORE_PROBE_COMMAND:-}" ]; then
  # A test probe receives only the scratch path, never the decryption key.
  STUDIO_SCRATCH_DIR="$scratch" bash -c "$STUDIO_RESTORE_PROBE_COMMAND"
else
  # Since AGT-3379 an Owner or Operator without a second factor gets only an
  # enrollment session, so the proof signs in with password and current TOTP code.
  for credential in STUDIO_RESTORE_USER_FILE STUDIO_RESTORE_PASSWORD_FILE STUDIO_RESTORE_TOTP_SECRET_FILE; do
    [ -r "${!credential:-}" ] || {
      echo 'Set STUDIO_RESTORE_USER_FILE, STUDIO_RESTORE_PASSWORD_FILE and STUDIO_RESTORE_TOTP_SECRET_FILE for the sign-in probe' >&2
      exit 2
    }
    case "$(realpath "${!credential}")" in "$scratch"/*|"$stack"/*)
      echo "$credential must be kept separately from the installation" >&2; exit 2 ;;
    esac
  done
  edge_digest="$(sed -n 's/^STUDIO_EDGE_DIGEST=//p' "$scratch/stack/.env" | tail -n 1)"
  mkdir -p "$scratch/data/api-tmp"
  docker run --rm --user 0 -v "$scratch/data:/site" --entrypoint chown \
    "caddy@sha256:$edge_digest" -R 10001:10001 /site
  started=true
  docker compose --project-name "$project" --project-directory "$scratch/stack" \
    --env-file "$scratch/stack/.env" -f "$scratch/stack/compose.yaml" up -d
  port="$(docker compose --project-name "$project" --project-directory "$scratch/stack" \
    --env-file "$scratch/stack/.env" -f "$scratch/stack/compose.yaml" port edge 443 | sed 's/.*://')"
  # Credentials and the session response stay outside the scratch installation.
  probe="$(mktemp -d)"
  signed_in=false
  for _ in $(seq 1 30); do
    # The code is computed per attempt so a slow start cannot outlive its time step.
    python3 - "$probe/login.json" "$STUDIO_RESTORE_USER_FILE" "$STUDIO_RESTORE_PASSWORD_FILE" \
      "$STUDIO_RESTORE_TOTP_SECRET_FILE" <<'PY'
import base64, hashlib, hmac, json, pathlib, struct, sys, time
target, user, password, secret = sys.argv[1:]
secret = pathlib.Path(secret).read_text().strip().upper()
key = base64.b32decode(secret + '=' * (-len(secret) % 8))
digest = hmac.new(key, struct.pack('>q', int(time.time()) // 30), hashlib.sha1).digest()
offset = digest[-1] & 15
code = (struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7fffffff) % 1000000
pathlib.Path(target).write_text(json.dumps({
    'username': pathlib.Path(user).read_text().strip(),
    'password': pathlib.Path(password).read_text().strip(),
    'secondFactorCode': f'{code:06d}',
}))
PY
    curl --silent --show-error --insecure --max-time 3 \
      --resolve "restore.localhost:$port:127.0.0.1" \
      --header 'Content-Type: application/json' \
      --header "Origin: https://restore.localhost:$port" --data-binary "@$probe/login.json" \
      "https://restore.localhost:$port/api/auth/login" > "$probe/response.json" 2> /dev/null || true
    rm -f "$probe/login.json"
    verdict="$(python3 - "$probe/response.json" <<'PY'
import json, sys
try:
    response = json.load(open(sys.argv[1]))
except (OSError, ValueError):
    print('starting')
    raise SystemExit
if not isinstance(response, dict):
    print('starting')
elif response.get('authenticated') is True and response.get('secondFactorEnrollmentRequired') is False:
    print('signed-in')
elif response.get('authenticated') is True:
    print('enrollment-only')
else:
    print('refused')
PY
)"
    case "$verdict" in
      signed-in) signed_in=true; break ;;
      enrollment-only) reason=signin-enrollment-only
        echo 'The restored account has no second factor; the sign-in got only an enrollment session' >&2; exit 1 ;;
      refused) reason=signin-refused; echo 'The restored API refused the sign-in' >&2; exit 1 ;;
    esac
    sleep 2
  done
  [ "$signed_in" = true ] || { echo 'Scratch sign-in did not succeed' >&2; exit 1; }
fi
result=pass
reason=restored-and-signed-in
echo 'Encrypted backup restored; scratch sign-in passed.'
