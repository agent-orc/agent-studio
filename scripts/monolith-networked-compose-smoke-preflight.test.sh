#!/usr/bin/env bash
# Exercise the smoke setup against a Docker stub that knows only the Caddy
# image used by the smoke. The test stops after setup reaches Compose startup.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/repo/scripts" "$work/repo/deploy/compose/monolith-networked" "$work/bin"
cp "$repo_root/scripts/monolith-networked-compose-smoke.sh" "$work/repo/scripts/"
cp "$repo_root/deploy/compose/monolith-networked/setup.sh" \
  "$repo_root/deploy/compose/monolith-networked/compose.yaml" \
  "$work/repo/deploy/compose/monolith-networked/"
cp "$repo_root/VERSION" "$work/repo/VERSION"
export DOCKER_TEST_DIGEST="$(openssl rand -hex 32)"
export DOCKER_TEST_LOG="$work/docker.log"
cat > "$work/bin/docker" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$DOCKER_TEST_LOG"
if [ "${1:-}" = pull ]; then exit 0; fi
if [ "${1:-}" = image ] && [ "${2:-}" = inspect ]; then
  printf 'caddy@sha256:%s\n' "$DOCKER_TEST_DIGEST"
  exit 0
fi
if [ "${1:-}" = run ]; then
  case " $* " in
    *" caddy@sha256:$DOCKER_TEST_DIGEST "*|*" caddy:2-alpine "*) exit 0 ;;
    *) echo 'Unknown Caddy image digest in ownership helper' >&2; exit 1 ;;
  esac
fi
if [ "${1:-}" = compose ]; then
  case " $* " in *' up -d --no-build'*) exit 77 ;; esac
  exit 0
fi
echo "Unexpected Docker call: $*" >&2
exit 1
STUB
chmod +x "$work/bin/docker"
if PATH="$work/bin:$PATH" WEBSITE_REUSE_WEBSITE_IMAGES=1 \
  bash "$work/repo/scripts/monolith-networked-compose-smoke.sh" > "$work/output" 2>&1; then
  echo 'Smoke unexpectedly completed with a stub Docker daemon' >&2
  exit 1
fi
if ! grep -Fq ' up -d --no-build' "$DOCKER_TEST_LOG"; then
  cat "$work/output" >&2
  echo 'Smoke failed before Compose startup' >&2
  exit 1
fi
echo 'Website smoke setup used the available Caddy image digest.'
