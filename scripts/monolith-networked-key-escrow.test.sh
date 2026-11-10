#!/usr/bin/env bash
# Escrow commands of the networked stack (AGT-3738): `print` fails with a
# message when the helper container cannot run or returns no 32-byte key,
# and the helper image is the edge image pinned by digest, never a tag.
# A fake `docker` on PATH stands in for the daemon, so no Docker is needed.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stack="$repo_root/deploy/compose/monolith-networked"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/stack" "$work/data" "$work/keys" "$work/bin"
cp "$stack/backup-key.sh" "$stack/secret-key.sh" "$work/stack/"
digest="$(openssl rand -hex 32)"
printf 'STUDIO_DATA_DIR=%s/data\nSTUDIO_SECRETS_KEY_FILE=%s/keys/secrets.kek\nSTUDIO_BACKUP_KEY_DIR=%s/keys/backup\nSTUDIO_EDGE_DIGEST=%s\n' \
  "$work" "$work" "$work" "$digest" > "$work/stack/.env"
key="$(head -c 32 /dev/urandom | base64)"

# FAKE_DOCKER selects the outcome; every call records its arguments.
cat > "$work/bin/docker" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$FAKE_DOCKER_LOG"
case "$FAKE_DOCKER" in
  denied) echo 'permission denied while trying to connect to the Docker daemon socket' >&2; exit 1 ;;
  empty) exit 0 ;;
  short) printf 'c2hvcnQ=\n' ;;
  key) printf '%s\n' "$FAKE_KEY" ;;
esac
EOF
chmod +x "$work/bin/docker"
export PATH="$work/bin:$PATH" FAKE_DOCKER_LOG="$work/docker.log" FAKE_KEY="$key"

fail() { echo "$1" >&2; exit 1; }

print_key() {
  case "$1" in
    backup) bash "$work/stack/backup-key.sh" print ;;
    secret) bash "$work/stack/secret-key.sh" print "$work/data" ;;
  esac
}

for which in backup secret; do
  for outcome in denied empty short; do
    if FAKE_DOCKER="$outcome" print_key "$which" > "$work/copy" 2> "$work/err"; then
      fail "$which print succeeded although docker returned '$outcome'"
    fi
    [ ! -s "$work/copy" ] || fail "$which print wrote output although docker returned '$outcome'"
    grep -q 'no copy of the key' "$work/err" || fail "$which print gave no message for '$outcome': $(cat "$work/err")"
    if bash "$work/stack/$which-key.sh" id "$work/copy" > /dev/null 2>&1; then
      fail "$which id accepted the empty copy left by a failed print"
    fi
  done
  grep -q 'Docker access' <(FAKE_DOCKER=denied print_key "$which" 2>&1 >/dev/null) \
    || fail "$which print does not name Docker access when the daemon refuses"

  FAKE_DOCKER=key print_key "$which" > "$work/copy"
  [ "$(cat "$work/copy")" = "$key" ] || fail "$which print did not return the key"
  [ "$(bash "$work/stack/$which-key.sh" id "$work/copy" | wc -c)" -eq 17 ] || fail "$which id did not print a 16-character id"
done

# Every container runs the edge image by digest.
images="$(grep -o 'caddy[^ ]*' "$work/docker.log" | sort -u)"
[ "$images" = "caddy@sha256:$digest" ] || fail "helper image is not pinned by digest: $images"
FAKE_DOCKER=empty bash "$work/stack/backup-key.sh" install "$work/copy"
FAKE_DOCKER=empty bash "$work/stack/secret-key.sh" install "$work/data" "$work/copy"
[ "$(grep -o 'caddy[^ ]*' "$work/docker.log" | sort -u)" = "caddy@sha256:$digest" ] || fail 'install does not use the pinned helper image'

sed -i 's/^STUDIO_EDGE_DIGEST=.*/STUDIO_EDGE_DIGEST=2-alpine/' "$work/stack/.env"
for which in backup secret; do
  if FAKE_DOCKER=key print_key "$which" > /dev/null 2> "$work/err"; then
    fail "$which print ran without a pinned edge digest"
  fi
  grep -q 'STUDIO_EDGE_DIGEST' "$work/err" || fail "$which print does not name STUDIO_EDGE_DIGEST"
done

echo 'Key escrow checks passed'
