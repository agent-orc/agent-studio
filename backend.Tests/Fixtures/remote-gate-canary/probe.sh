#!/bin/sh
set -eu
test "$(uname -s)" = Linux
printf "REMOTE_GATE_CANARY_OK\n"
