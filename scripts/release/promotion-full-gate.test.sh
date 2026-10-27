#!/usr/bin/env bash
set -Eeuo pipefail

source_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
repo="$test_root/repo"
fake_bin="$test_root/bin"
mkdir -p "$repo/scripts/release" "$repo/frontend" "$fake_bin"
cp "$source_root/scripts/release/promotion-full-gate.sh" "$repo/scripts/release/"
for script in locked-restore.test.sh release-scripts.test.sh; do
  printf '#!/bin/sh\nexit 0\n' > "$repo/scripts/release/$script"
  chmod +x "$repo/scripts/release/$script"
done
printf 'schemaVersion: 1\n' > "$repo/.project-fixture"
git -C "$repo" init --quiet
git -C "$repo" add .
git -C "$repo" -c user.name=Test -c user.email=test@example.invalid commit --quiet -m candidate
sha=$(git -C "$repo" rev-parse HEAD)
digest=$(printf 'b%.0s' {1..64})
printf 'test-token\n' > "$test_root/token"

cat > "$fake_bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$FAKE_DOTNET_CALLS"
if [[ "$1" == --version ]]; then printf '10.0.0\n'; fi
if [[ "$1" == test && "${FAKE_TEST_RED:-0}" == 1 ]]; then exit 1; fi
FAKE
cat > "$fake_bin/corepack" <<'FAKE'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$FAKE_COREPACK_CALLS"
if [[ "$*" == 'npm --version' ]]; then printf '11.0.0\n'; fi
FAKE
cat > "$fake_bin/node" <<'FAKE'
#!/usr/bin/env bash
printf 'v22.0.0\n'
FAKE
cat > "$fake_bin/curl" <<'FAKE'
#!/usr/bin/env bash
set -eu
url=
body=
while (($#)); do
  case "$1" in
    -H)
      if [[ "$2" == @* ]]; then grep -Fq 'Authorization: Bearer test-token' "${2#@}"; fi
      shift 2 ;;
    --data) body=$2; shift 2 ;;
    http*) url=$1; shift ;;
    *) shift ;;
  esac
done
if [[ "$url" == */release-candidates/* ]]; then
  printf '{"mode":"required","postMergeGreen":true,"commandDigest":"%s","verdict":' "$FAKE_DIGEST"
  if [[ "$FAKE_VERDICT_STATUS" == missing ]]; then printf 'null}\n'
  else printf '{"status":"%s"}}\n' "$FAKE_VERDICT_STATUS"; fi
else
  printf '%s\n' "$body" > "$FAKE_PUBLISHED_VERDICT"
  printf '%s\n' "$body"
fi
FAKE
chmod +x "$fake_bin"/*
export PATH="$fake_bin:$PATH" FAKE_DOTNET_CALLS="$test_root/dotnet-calls" \
  FAKE_COREPACK_CALLS="$test_root/corepack-calls" FAKE_PUBLISHED_VERDICT="$test_root/verdict.json" \
  FAKE_DIGEST="$digest"
FAKE_VERDICT_STATUS=green "$repo/scripts/release/promotion-full-gate.sh" --repo "$repo" \
  --project Demo --verdict-api http://example.invalid --token-file "$test_root/token" \
  > "$test_root/reuse.log"
grep -Fxq PROMOTION_FULL_GATE=reused "$test_root/reuse.log"
grep -Fxq PROMOTION_FULL_GATE=passed "$test_root/reuse.log"
! grep -q '^test ' "$FAKE_DOTNET_CALLS"

: > "$FAKE_DOTNET_CALLS"
FAKE_VERDICT_STATUS=missing "$repo/scripts/release/promotion-full-gate.sh" --repo "$repo" \
  --project Demo --verdict-api http://example.invalid --token-file "$test_root/token" \
  > "$test_root/mismatch.log"
test "$(grep -c '^test ' "$FAKE_DOTNET_CALLS")" -eq 9
grep -Fq '"status": "green"' "$FAKE_PUBLISHED_VERDICT"
grep -Fxq PROMOTION_FULL_GATE=passed "$test_root/mismatch.log"
! grep -Fxq PROMOTION_FULL_GATE=reused "$test_root/mismatch.log"

: > "$FAKE_DOTNET_CALLS"
if FAKE_TEST_RED=1 FAKE_VERDICT_STATUS=missing "$repo/scripts/release/promotion-full-gate.sh" \
  --repo "$repo" --project Demo --verdict-api http://example.invalid \
  --token-file "$test_root/token" > "$test_root/red.log"; then
  printf 'A red shard passed the release gate.\n' >&2
  exit 1
fi
grep -Fq '"status": "red"' "$FAKE_PUBLISHED_VERDICT"
printf 'promotion verdict reuse, digest miss, and red shard passed (%s)\n' "$sha"
