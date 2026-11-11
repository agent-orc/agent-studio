#!/usr/bin/env bash
# Runs inside the API image as UID 10001 (started by restore.sh) and reads the
# decrypted backup tar stream from stdin.
#
#   restore-apply.sh manifest  print manifest.env, a separator line, then checkouts.tsv
#   restore-apply.sh unpack    restore into the empty /data/workspace and /data/projects;
#                              checkouts cloned by restore.sh are under /restore-clones/<index>
set -euo pipefail

log() { printf 'restore: %s\n' "$*" >&2; }
fail() { log "$*"; exit 1; }

case "${1:-}" in
  manifest)
    work="$(mktemp -d)"
    tar --extract --gzip --file - -C "$work" --occurrence=1 \
      studio-backup/manifest.env studio-backup/checkouts.tsv
    cat "$work/studio-backup/manifest.env"
    echo '--'
    cat "$work/studio-backup/checkouts.tsv"
    exit 0
    ;;
  unpack) ;;
  *) fail 'usage: restore-apply.sh manifest|unpack' ;;
esac

# Restore only into the state setup.sh leaves: a workspace that holds just the
# installer's single commit of .gitignore and no project data.
if [ -n "$(find /data/workspace -mindepth 1 -maxdepth 1 ! -name .git ! -name .gitignore -print -quit)" ]; then
  fail 'the workspace is not empty; restore needs a fresh stack from setup.sh that has not been started'
fi
if [ -d /data/workspace/.git ] && [ "$(git -C /data/workspace rev-list --all --count 2>/dev/null || echo 0)" -gt 1 ]; then
  fail 'the workspace repository has history; restore needs a fresh stack from setup.sh'
fi
if [ -e /data/workspace/.gitignore ] && [ -n "$(git -C /data/workspace status --porcelain 2>/dev/null || echo changed)" ]; then
  fail 'the workspace has changes; restore needs a fresh stack from setup.sh'
fi
if [ -n "$(find /data/projects -mindepth 1 -maxdepth 1 -print -quit)" ]; then
  fail 'the projects directory is not empty; restore needs a fresh stack from setup.sh'
fi
# The archived workspace and its repository replace setup.sh's. Keep them
# aside until the restore succeeds; a failed restore empties the targets and
# puts them back, so restore.sh can run again on the same stack.
setup_held="$(mktemp -d)"
for entry in .git .gitignore; do
  if [ -e "/data/workspace/$entry" ]; then mv "/data/workspace/$entry" "$setup_held/"; fi
done
finish() {
  status=$?
  if [ "$status" -ne 0 ]; then
    find /data/workspace /data/projects -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    cp -a "$setup_held/." /data/workspace/
    log 'restore failed; the workspace and projects directory are back to the state setup.sh left'
  fi
  rm -rf "$setup_held"
}
trap finish EXIT

staging=projects/.studio-restore
# --no-overwrite-dir keeps owner and mode of the mounted roots from setup.sh.
tar --extract --gzip --file - -C /data --no-same-owner --preserve-permissions --no-overwrite-dir \
  --transform "s,^studio-backup,$staging," \
  studio-backup workspace projects
[ -f "/data/$staging/checkouts.tsv" ] || fail 'the archive has no studio-backup/checkouts.tsv'

while IFS=$'\t' read -r index root origin branch bundle; do
  [ -n "$index" ] || continue
  case "$root" in projects/*) ;; *) fail "unexpected checkout path $root" ;; esac
  case "/$root/" in */../*|*/./*) fail "unexpected checkout path $root" ;; esac
  target="/data/$root"
  # Task storage inside the checkout may already be in place from the archive.
  mkdir -p "$target"
  if [ "$origin" != - ]; then
    [ -d "/restore-clones/$index/.git" ] || fail "no clone of $origin for $root"
    cp -a "/restore-clones/$index/." "$target/"
    git -C "$target" remote set-url origin "$origin"
    if [ "$branch" != - ] && [ "$(git -C "$target" symbolic-ref -q --short HEAD || true)" != "$branch" ]; then
      git -C "$target" checkout -q "$branch" 2>/dev/null \
        || log "$root: branch $branch is not on $origin; the checkout stays on the remote default branch"
    fi
    if [ "$bundle" != - ]; then
      git -C "$target" fetch -q "/data/$staging/bundles/$bundle" '+refs/agent-studio/*:refs/agent-studio/*'
    fi
  else
    git init -q "$target"
    if [ "$bundle" != - ]; then
      git -C "$target" fetch -q --update-head-ok "/data/$staging/bundles/$bundle" '+refs/*:refs/*'
      if [ "$branch" != - ]; then git -C "$target" symbolic-ref HEAD "refs/heads/$branch"; fi
      git -C "$target" rev-parse -q --verify HEAD > /dev/null && git -C "$target" reset -q --hard
    fi
  fi
  log "restored checkout $root with $(git -C "$target" for-each-ref refs/agent-studio/ | wc -l) Studio ref(s)"
done < "/data/$staging/checkouts.tsv"

rm -rf "/data/$staging"
# Host-level archives use this extraction path rather than BackupRestore.
# Apply the same session fence and Owner notice after extraction succeeds.
node <<'NODE'
const fs = require('node:fs');
const path = require('node:path');
const folder = '/data/workspace/.security';
const statePath = path.join(folder, 'access-state.json');
fs.mkdirSync(folder, { recursive: true, mode: 0o700 });
function writeAtomic(file, value) {
  const temporary = `${file}.restore-tmp`;
  fs.writeFileSync(temporary, JSON.stringify(value, null, 2), { mode: 0o600 });
  fs.renameSync(temporary, file);
}
if (fs.existsSync(statePath)) {
  const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
  if (!state || Array.isArray(state) || typeof state !== 'object') throw Error('restored access state is not a JSON object');
  for (const key of Object.keys(state)) if (key.toLowerCase() === 'sessions') delete state[key];
  state.Sessions = [];
  writeAtomic(statePath, state);
}
writeAtomic(path.join(folder, 'restore-notice.json'), {
  ArchiveId: process.env.RESTORE_ARCHIVE_ID,
  ArchiveCreatedAt: process.env.RESTORE_ARCHIVE_CREATED,
  RestoredAt: new Date().toISOString(),
});
NODE
log 'workspace, task storage and Studio refs restored'
