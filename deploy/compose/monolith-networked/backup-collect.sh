#!/usr/bin/env bash
# Runs inside the API image as UID 10001 (started by backup.sh) and writes a
# gzip-compressed tar stream of the Studio state to stdout. Nothing leaves
# the container unencrypted except through that pipe into age.
#
# Archive layout:
#   workspace/                 the whole workspace, including .git, .metadata
#                              and .security (access state, sealed secrets)
#   projects/                  everything under /data/projects except project
#                              checkouts and worktrees (e.g. .task-storage)
#   projects/<checkout>/...    registered task storage that lies inside a checkout
#   studio-backup/manifest.env format, time, version and secret store key id
#   studio-backup/checkouts.tsv one line per checkout: index, path, origin, branch, bundle
#   studio-backup/bundles/     git bundles of refs/agent-studio/* per checkout
#                              (all refs when the checkout has no origin)
#
# Checkouts come back from their remotes on restore; only the Studio refs,
# which exist nowhere else, travel in the archive. The secret store key is
# mounted only to record its fingerprint and is never archived.
set -euo pipefail

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
meta="$stage/studio-backup"
mkdir -p "$meta/bundles"
cd /data

log() { printf 'backup: %s\n' "$*" >&2; }

# Removes a password or token from an https URL so the archive does not carry it.
strip_credentials() {
  printf '%s' "$1" | sed -E 's#^([a-zA-Z][a-zA-Z0-9+.-]*://)[^/@]*:[^/@]*@#\1#'
}

kek_id=none
if [ -r /run/secrets/studio-secrets.kek ]; then
  kek_id="$({ printf 'agent-studio/kek-id/v1|'; tr -d '\r\n' < /run/secrets/studio-secrets.kek | base64 -d; } | sha256sum | cut -c1-16)"
fi
{
  echo 'format=1'
  echo "created=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "studio_version=${STUDIO_VERSION:-unknown}"
  echo "domain=${STUDIO_DOMAIN:-unknown}"
  echo "secret_key_id=$kek_id"
} > "$meta/manifest.env"

# A checkout or worktree is a directory with a .git entry. Its root is
# recorded and its content is not archived.
mapfile -d '' roots < <(find projects -mindepth 1 -type d -exec test -e '{}/.git' \; -print0 -prune 2>/dev/null || true)

checkouts=()
index=0
: > "$meta/checkouts.tsv"
for root in "${roots[@]}"; do
  case "$root" in *$'\t'*|*$'\n'*) log "refusing checkout path with a tab or newline: $root"; exit 1;; esac
  if [ -f "$root/.git" ]; then
    log "skipping worktree $root; its branch lives in the main checkout and on the remote"
    continue
  fi
  index=$((index + 1))
  checkouts+=("$root")
  origin="$(git -C "$root" config --get remote.origin.url || true)"
  origin="$(strip_credentials "$origin")"
  branch="$(git -C "$root" symbolic-ref -q --short HEAD || true)"
  bundle=-
  if [ -z "$origin" ]; then
    # Nothing to clone from: keep every ref.
    if [ -n "$(git -C "$root" for-each-ref --count=1)" ]; then
      bundle="$index.bundle"
      git -C "$root" bundle create -q "$meta/bundles/$bundle" --all
    fi
  elif [ -n "$(git -C "$root" for-each-ref --count=1 refs/agent-studio/)" ]; then
    bundle="$index.bundle"
    git -C "$root" bundle create -q "$meta/bundles/$bundle" --glob='refs/agent-studio/*'
  fi
  printf '%s\t%s\t%s\t%s\t%s\n' "$index" "$root" "${origin:--}" "${branch:--}" "$bundle" >> "$meta/checkouts.tsv"
  log "checkout $root: origin ${origin:-none}, studio refs bundle $bundle"
done

# Registered task storage inside a checkout is not in the remote; archive it.
storage_inside_checkouts() {
  [ "${#checkouts[@]}" -gt 0 ] || return 0
  node - "${checkouts[@]}" <<'JS'
const fs = require('fs');
const path = require('path');
const checkouts = process.argv.slice(2).map(root => path.join('/data', root) + '/');
const field = (object, name) => object?.[name] ?? object?.[name[0].toLowerCase() + name.slice(1)];
let registry = null;
for (const file of ['/data/workspace/.metadata/projects.json', '/data/workspace/.metadata/projects.json.bak']) {
  try { registry = JSON.parse(fs.readFileSync(file, 'utf8')); break; } catch { /* try the next copy */ }
}
for (const project of field(registry, 'Projects') ?? []) {
  const storage = field(project, 'StorageLocation');
  if (typeof storage !== 'string' || !storage) continue;
  const full = path.resolve(storage) + '/';
  if (checkouts.some(root => full.startsWith(root)) && fs.existsSync(full)) {
    process.stdout.write(path.relative('/data', full) + '\0');
  }
}
JS
}

{
  find workspace -print0
  # Includes the projects directory itself, so the member exists even when
  # it holds nothing but checkouts.
  find projects \( -type d ! -path projects -exec test -e '{}/.git' \; \) -prune -o -print0
  while IFS= read -r -d '' storage; do
    log "including task storage $storage inside its checkout"
    find "$storage" -print0
  done < <(storage_inside_checkouts)
} > "$stage/files"

# The manifest goes first so restore can read it without decrypting the rest.
tar --create --gzip --file - --numeric-owner \
  -C "$stage" studio-backup/manifest.env studio-backup/checkouts.tsv studio-backup/bundles \
  --null --no-recursion -C /data -T "$stage/files"
