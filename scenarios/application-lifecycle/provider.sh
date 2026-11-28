#!/usr/bin/env bash
# Deterministic provider for the isolated coding runner, adapted from
# testsupport/scenario/scenario-coding-agent.sh. The fixture never accesses an account.
set -eu
case "${1:-}" in
  --version) echo 'codex-cli 0.144.1'; exit 0 ;;
  login) echo 'Logged in using ChatGPT (fixture)'; exit 0 ;;
  auth) echo 'Logged in (fixture)'; exit 0 ;;
esac
case "$(pwd -P)/" in "${SCENARIO_WORK_ROOT:?}/"*) ;; *) exit 69 ;; esac
if [[ "${SCENARIO_WORK_ROLE:-}" == review ]]; then
  [[ -f app.test.mjs ]] || exit 69
  node --test app.test.mjs >&2
  if [[ "${0##*/}" == claude ]]; then
    echo '[[ASPECT_VERDICT: status=pass; summary=Click Counter test passed.]]'
  else
    echo '{"type":"item.completed","item":{"type":"agent_message","text":"[[ASPECT_VERDICT: status=pass; summary=Click Counter test passed.]]"}}'
  fi
  exit 0
fi
if [[ "$PWD" == */project-chat-* || "$*" == *read-only* ]]; then
  echo '{"type":"item.completed","item":{"type":"agent_message","text":"Fixture provider received the Click Counter request. Confirm the change in Studio."}}'
  exit 0
fi
[[ -f app.test.mjs && -f package.json ]] || exit 69
node "$(dirname "$(readlink -f "$0")")/evolve.mjs"
node --test app.test.mjs
node build.mjs
echo '{"type":"agent_message","text":"Built and tested the Click Counter fixture."}'
echo '[[TASK_DONE]]'
