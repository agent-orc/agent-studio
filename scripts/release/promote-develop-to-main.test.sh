#!/usr/bin/env bash

set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
driver_source="$repo_root/scripts/release/promote-develop-to-main.sh"
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-tests)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

make_fixture() {
  local name=$1
  local gate_mode=$2
  local topology_mode=${3:-linear}
  local fixture="$test_root/$name"
  local remote="$fixture/remote.git"
  local seed="$fixture/seed"
  local operator="$fixture/operator"

  mkdir -p "$fixture"
  git init --bare --quiet "$remote"
  git init --quiet "$seed"
  git -C "$seed" config user.name 'Promotion Test'
  git -C "$seed" config user.email 'promotion-test@example.invalid'
  mkdir -p "$seed/scripts/release"
  cp "$driver_source" "$seed/scripts/release/promote-develop-to-main.sh"
  chmod +x "$seed/scripts/release/promote-develop-to-main.sh"

  case "$gate_mode" in
    pass)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
test -d "${2:-}"
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    fail)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
printf '%s\n' 'fixture gate failed' >&2
exit 9
EOF
      ;;
    move-develop)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
candidate=$2
remote_url=$(git -C "$candidate" remote get-url origin)
advance_checkout=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-develop-advance)
trap 'rm -rf -- "$advance_checkout"' EXIT HUP INT TERM
git clone --quiet --branch develop "$remote_url" "$advance_checkout"
git -C "$advance_checkout" config user.name 'Promotion Test Gate'
git -C "$advance_checkout" config user.email 'promotion-test-gate@example.invalid'
git -C "$advance_checkout" commit --quiet --allow-empty -m 'develop advances during gate'
git -C "$advance_checkout" push --quiet origin HEAD:refs/heads/develop
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    move-main)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
candidate=$2
remote_url=$(git -C "$candidate" remote get-url origin)
advance_checkout=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-main-advance)
trap 'rm -rf -- "$advance_checkout"' EXIT HUP INT TERM
git clone --quiet --branch main "$remote_url" "$advance_checkout"
git -C "$advance_checkout" config user.name 'Promotion Test Gate'
git -C "$advance_checkout" config user.email 'promotion-test-gate@example.invalid'
git -C "$advance_checkout" commit --quiet --allow-empty -m 'main advances during gate'
git -C "$advance_checkout" push --quiet origin HEAD:refs/heads/main
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    incomplete)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
printf '%s\n' 'fixture omitted the completion marker'
EOF
      ;;
    *)
      printf 'Unknown gate fixture: %s\n' "$gate_mode" >&2
      return 2
      ;;
  esac
  chmod +x "$seed/scripts/release/promotion-full-gate.sh"
  printf '%s\n' base > "$seed/payload.txt"
  git -C "$seed" add .
  git -C "$seed" commit --quiet -m 'base'
  git -C "$seed" branch -M main
  git -C "$seed" remote add origin "$remote"
  git -C "$seed" push --quiet -u origin main
  git --git-dir="$remote" symbolic-ref HEAD refs/heads/main

  if [[ "$topology_mode" == diverged ]]; then
    printf '%s\n' main > "$seed/payload.txt"
    git -C "$seed" commit --quiet -am 'main-only change'
    git -C "$seed" push --quiet origin main
    git -C "$seed" checkout --quiet -b develop HEAD~1
  else
    git -C "$seed" checkout --quiet -b develop
  fi
  printf '%s\n' develop > "$seed/payload.txt"
  printf '%s\n' "develop-$name" > "$seed/develop.txt"
  # Historical integrated content may contain whitespace findings. Promotion
  # records them for review but leaves pass/fail authority with the full gate.
  printf '%s  \n' "historical-$name" > "$seed/historical-whitespace.txt"
  git -C "$seed" add .
  git -C "$seed" commit --quiet -m 'develop work'
  git -C "$seed" push --quiet -u origin develop

  git clone --quiet --branch develop "$remote" "$operator"
  git -C "$operator" config user.name 'Promotion Operator'
  git -C "$operator" config user.email 'promotion-operator@example.invalid'
  printf '%s\n' "$operator"
}

run_expect_rc() {
  local expected=$1
  shift
  set +e
  "$@"
  local actual=$?
  set -e
  if [[ "$actual" != "$expected" ]]; then
    printf 'Expected rc=%s, got rc=%s: %s\n' "$expected" "$actual" "$*" >&2
    return 1
  fi
}

# A dry run against a local bare remote records the exact candidate without
# running the gate or changing either remote ref.
preview_operator=$(make_fixture preview pass)
preview_remote="$test_root/preview/remote.git"
preview_evidence="$test_root/preview/evidence"
preview_main=$(git --git-dir="$preview_remote" rev-parse refs/heads/main)
"$preview_operator/scripts/release/promote-develop-to-main.sh" \
  --dry-run --tag release/test-preview --evidence-dir "$preview_evidence" >/dev/null
test "$(git --git-dir="$preview_remote" rev-parse refs/heads/main)" = "$preview_main"
! git --git-dir="$preview_remote" show-ref --verify --quiet refs/tags/release/test-preview
grep -q '"status":"preview"' "$preview_evidence/promotion-record.json"
grep -q '"gate":"not-run"' "$preview_evidence/promotion-record.json"
printf '%s\n' 'local bare remote dry-run passed'

# A normal execute run promotes the exact develop tip, produces one annotated
# marker, records full-gate evidence, and performs an atomic remote update.
green_operator=$(make_fixture green pass)
green_remote="$test_root/green/remote.git"
green_evidence="$test_root/green/evidence"
old_main=$(git --git-dir="$green_remote" rev-parse refs/heads/main)
develop=$(git --git-dir="$green_remote" rev-parse refs/heads/develop)
"$green_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-green --required-ancestor "$develop" \
  --evidence-dir "$green_evidence" >/dev/null
new_main=$(git --git-dir="$green_remote" rev-parse refs/heads/main)
test "$new_main" = "$develop"
git --git-dir="$green_remote" merge-base --is-ancestor "$old_main" "$new_main"
test "$(git --git-dir="$green_remote" cat-file -t refs/tags/release/test-green)" = tag
test "$(git --git-dir="$green_remote" rev-parse refs/tags/release/test-green^{})" = "$new_main"
grep -q '"status":"promoted"' "$green_evidence/promotion-record.json"
grep -q '"atomicPush":true' "$green_evidence/promotion-record.json"
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$green_evidence/full-gate.log"
grep -q 'historical-whitespace.txt' "$green_evidence/candidate-whitespace-review.txt"
printf '%s\n' 'local bare remote execute passed'

# Annotated tags use the promotion identity even when HOME is empty and neither
# repository nor process environment provides a Git identity.
identity_operator=$(make_fixture no-identity pass)
identity_remote="$test_root/no-identity/remote.git"
identity_evidence="$test_root/no-identity/evidence"
identity_home="$test_root/no-identity/empty-home"
identity_xdg="$test_root/no-identity/empty-xdg"
mkdir -p "$identity_home" "$identity_xdg"
git -C "$identity_operator" config --unset-all user.name
git -C "$identity_operator" config --unset-all user.email
test -z "$(HOME="$identity_home" XDG_CONFIG_HOME="$identity_xdg" \
  GIT_CONFIG_NOSYSTEM=1 git -C "$identity_operator" config --get user.name || true)"
test -z "$(HOME="$identity_home" XDG_CONFIG_HOME="$identity_xdg" \
  GIT_CONFIG_NOSYSTEM=1 git -C "$identity_operator" config --get user.email || true)"
env -u GIT_AUTHOR_NAME -u GIT_AUTHOR_EMAIL \
  -u GIT_COMMITTER_NAME -u GIT_COMMITTER_EMAIL -u EMAIL \
  HOME="$identity_home" XDG_CONFIG_HOME="$identity_xdg" \
  GIT_CONFIG_NOSYSTEM=1 \
  "$identity_operator/scripts/release/promote-develop-to-main.sh" \
    --execute --tag release/test-no-identity \
    --evidence-dir "$identity_evidence" >/dev/null
identity_candidate=$(git --git-dir="$identity_remote" rev-parse refs/heads/develop)
test "$(git --git-dir="$identity_remote" rev-parse refs/heads/main)" = "$identity_candidate"
test "$(git --git-dir="$identity_remote" cat-file -t refs/tags/release/test-no-identity)" = tag
test "$(git --git-dir="$identity_remote" for-each-ref \
  --format='%(taggername)|%(taggeremail)' refs/tags/release/test-no-identity)" \
  = 'Agent Studio Promotion|<promotion@agent-studio.invalid>'
grep -q '"status":"promoted"' "$identity_evidence/promotion-record.json"
printf '%s\n' 'empty-HOME annotated tag execute passed'

# A tag creation failure after a passing gate leaves the remote unchanged and
# writes the failure, gate result, and Git error to the durable record.
tag_failure_operator=$(make_fixture tag-failure pass)
tag_failure_remote="$test_root/tag-failure/remote.git"
tag_failure_evidence="$test_root/tag-failure/evidence"
tag_failure_main=$(git --git-dir="$tag_failure_remote" rev-parse refs/heads/main)
run_expect_rc 6 env PROMOTION_TAGGER_NAME='<' \
  "$tag_failure_operator/scripts/release/promote-develop-to-main.sh" \
    --execute --tag release/test-tag-failure \
    --evidence-dir "$tag_failure_evidence" >/dev/null 2>&1
test "$(git --git-dir="$tag_failure_remote" rev-parse refs/heads/main)" = "$tag_failure_main"
! git --git-dir="$tag_failure_remote" show-ref --verify --quiet \
  refs/tags/release/test-tag-failure
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$tag_failure_evidence/full-gate.log"
grep -q '"status":"blocked-tag"' "$tag_failure_evidence/promotion-record.json"
grep -q '"gate":"passed"' "$tag_failure_evidence/promotion-record.json"
grep -q '"atomicPush":false' "$tag_failure_evidence/promotion-record.json"
grep -q '"error":"annotated tag creation failed with exit code 128:' \
  "$tag_failure_evidence/promotion-record.json"
grep -q 'name consists only of disallowed characters' "$tag_failure_evidence/tag.log"
printf '%s\n' 'simulated tag failure record passed'

# An atomic push rejection is likewise a complete post-gate record with the
# remote hook's error and without either remote ref changing.
push_failure_operator=$(make_fixture push-failure pass)
push_failure_remote="$test_root/push-failure/remote.git"
push_failure_evidence="$test_root/push-failure/evidence"
push_failure_main=$(git --git-dir="$push_failure_remote" rev-parse refs/heads/main)
cat > "$push_failure_remote/hooks/pre-receive" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' 'fixture push rejected' >&2
exit 1
EOF
chmod +x "$push_failure_remote/hooks/pre-receive"
run_expect_rc 6 "$push_failure_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-push-failure \
  --evidence-dir "$push_failure_evidence" >/dev/null 2>&1
test "$(git --git-dir="$push_failure_remote" rev-parse refs/heads/main)" = "$push_failure_main"
! git --git-dir="$push_failure_remote" show-ref --verify --quiet \
  refs/tags/release/test-push-failure
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$push_failure_evidence/full-gate.log"
grep -q '"status":"blocked-push"' "$push_failure_evidence/promotion-record.json"
grep -q '"gate":"passed"' "$push_failure_evidence/promotion-record.json"
grep -q '"atomicPush":false' "$push_failure_evidence/promotion-record.json"
grep -q '"error":"atomic main/tag push failed with exit code' \
  "$push_failure_evidence/promotion-record.json"
grep -q 'fixture push rejected' "$push_failure_evidence/push.log"
printf '%s\n' 'simulated atomic push failure record passed'

# A red or nominally green but incomplete gate cannot advance either ref.
for gate_mode in fail incomplete; do
  operator=$(make_fixture "gate-$gate_mode" "$gate_mode")
  remote="$test_root/gate-$gate_mode/remote.git"
  evidence="$test_root/gate-$gate_mode/evidence"
  before=$(git --git-dir="$remote" rev-parse refs/heads/main)
  run_expect_rc 4 "$operator/scripts/release/promote-develop-to-main.sh" \
    --execute --tag "release/test-$gate_mode" --evidence-dir "$evidence" >/dev/null 2>&1
  test "$(git --git-dir="$remote" rev-parse refs/heads/main)" = "$before"
  ! git --git-dir="$remote" show-ref --verify --quiet "refs/tags/release/test-$gate_mode"
done

# A develop advance during the gate is informational. The exact candidate that
# started the gate is promoted, while the newer develop commit waits for the
# next train.
move_operator=$(make_fixture ref-moved move-develop)
move_remote="$test_root/ref-moved/remote.git"
move_evidence="$test_root/ref-moved/evidence"
move_main=$(git --git-dir="$move_remote" rev-parse refs/heads/main)
move_candidate=$(git --git-dir="$move_remote" rev-parse refs/heads/develop)
"$move_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-ref-moved --evidence-dir "$move_evidence" >/dev/null
advanced_develop=$(git --git-dir="$move_remote" rev-parse refs/heads/develop)
test "$advanced_develop" != "$move_candidate"
test "$(git --git-dir="$move_remote" rev-parse refs/heads/main)" = "$move_candidate"
test "$(git --git-dir="$move_remote" rev-parse refs/tags/release/test-ref-moved^{})" = "$move_candidate"
git --git-dir="$move_remote" merge-base --is-ancestor "$move_main" "$move_candidate"
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$move_evidence/full-gate.log"
grep -Fq "develop advanced to $advanced_develop during gate; promoting gated candidate $move_candidate" \
  "$move_evidence/promotion.log"
grep -Fq "promoted develop=$move_candidate to main=$move_candidate with tag=release/test-ref-moved candidate=$move_candidate" \
  "$move_evidence/promotion.log"
grep -q '"status":"promoted"' "$move_evidence/promotion-record.json"

# A concurrent main advance that is not an ancestor of the gated candidate
# fails the final ancestry check. The external main commit remains untouched.
main_move_operator=$(make_fixture main-moved move-main)
main_move_remote="$test_root/main-moved/remote.git"
main_move_evidence="$test_root/main-moved/evidence"
main_move_candidate=$(git --git-dir="$main_move_remote" rev-parse refs/heads/develop)
run_expect_rc 5 "$main_move_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-main-moved --evidence-dir "$main_move_evidence" >/dev/null 2>&1
main_after_gate=$(git --git-dir="$main_move_remote" rev-parse refs/heads/main)
test "$main_after_gate" != "$main_move_candidate"
! git --git-dir="$main_move_remote" merge-base --is-ancestor "$main_after_gate" "$main_move_candidate"
! git --git-dir="$main_move_remote" show-ref --verify --quiet refs/tags/release/test-main-moved
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$main_move_evidence/full-gate.log"
grep -q '"status":"blocked-non-fast-forward"' "$main_move_evidence/promotion-record.json"
grep -q '"gate":"passed"' "$main_move_evidence/promotion-record.json"

# A develop tip that is not a descendant of main is never gated or pushed.
diverged_operator=$(make_fixture diverged pass diverged)
diverged_remote="$test_root/diverged/remote.git"
diverged_evidence="$test_root/diverged/evidence"
diverged_main=$(git --git-dir="$diverged_remote" rev-parse refs/heads/main)
diverged_candidate=$(git --git-dir="$diverged_remote" rev-parse refs/heads/develop)
if git --git-dir="$diverged_remote" merge-base --is-ancestor "$diverged_main" "$diverged_candidate"; then
  printf '%s\n' 'Diverged fixture unexpectedly produced a fast-forward candidate.' >&2
  exit 1
fi
run_expect_rc 3 "$diverged_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-diverged --evidence-dir "$diverged_evidence" >/dev/null 2>&1
test "$(git --git-dir="$diverged_remote" rev-parse refs/heads/main)" = "$diverged_main"
! git --git-dir="$diverged_remote" show-ref --verify --quiet refs/tags/release/test-diverged
grep -q '"status":"blocked-non-fast-forward"' "$diverged_evidence/promotion-record.json"
test ! -e "$diverged_evidence/full-gate.log"

if [[ -n ${PROMOTION_TEST_ARTIFACT_DIR:-} ]]; then
  mkdir -p "$PROMOTION_TEST_ARTIFACT_DIR"
  cp "$preview_evidence/promotion.log" \
    "$PROMOTION_TEST_ARTIFACT_DIR/local-bare-dry-run.log"
  cp "$preview_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/local-bare-dry-run-record.json"
  cp "$green_evidence/promotion.log" \
    "$PROMOTION_TEST_ARTIFACT_DIR/local-bare-execute.log"
  cp "$green_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/local-bare-execute-record.json"
  cp "$identity_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/empty-home-execute-record.json"
  cp "$tag_failure_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/simulated-tag-failure-record.json"
  cp "$tag_failure_evidence/tag.log" \
    "$PROMOTION_TEST_ARTIFACT_DIR/simulated-tag-failure.log"
  cp "$push_failure_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/simulated-push-failure-record.json"
  cp "$push_failure_evidence/push.log" \
    "$PROMOTION_TEST_ARTIFACT_DIR/simulated-push-failure.log"
fi

printf '%s\n' 'develop -> main promotion tests passed'
