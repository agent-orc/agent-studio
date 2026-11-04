#!/usr/bin/env bash
# Security model SM-2: end all operator access from the control plane host when
# the Studio UI is unreachable. Runs the API image's out-of-band command with
# the API's environment and volumes, so it works whether the api service is
# running or stopped. Arguments pass through, for example --reason TEXT.
set -euo pipefail

cd "$(dirname "$0")"
exec docker compose run --rm --no-deps --no-TTY api --end-operator-access "$@"
