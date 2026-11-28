#!/usr/bin/env bash
set -euo pipefail
workspace="${PRODUCT_FIXTURE_WORKSPACE:?start with scenarios/application-lifecycle/run.sh}"
export PW_E2E_TLS_CERT="${workspace}/localhost.crt" PW_E2E_TLS_KEY="${workspace}/localhost.key"
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 -subj '/CN=localhost' \
  -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' -keyout "${PW_E2E_TLS_KEY}" -out "${PW_E2E_TLS_CERT}" >/dev/null 2>&1
export PW_E2E_PROFILE=networked-https NODE_EXTRA_CA_CERTS="${PW_E2E_TLS_CERT}"
port="$(node scripts/find-free-port.mjs --count 1)"
node frontend/scripts/serve-prebuilt-frontend.mjs frontend/dist/frontend/browser "$port" "$PRODUCT_FIXTURE_URL" >"${workspace}/frontend.log" 2>&1 &
edge=$!
trap 'kill "$edge" 2>/dev/null || true; wait "$edge" || true' EXIT
studio="https://localhost:$port"
curl --retry 12 --retry-connrefused --retry-delay 1 --silent --fail --cacert "$PW_E2E_TLS_CERT" -o /dev/null "$studio/"
(umask 077; echo "${workspace##*/}-owner-code" > "${workspace}/code")
node scripts/release/product-scenario/local-owner.mjs --studio-url "$studio" --bootstrap-code-file "${workspace}/code" --out "${workspace}/owner.json"
status=0
node scenarios/application-lifecycle/"$1".mjs "$studio" || status=$?
node scenarios/application-lifecycle/report.mjs
exit "$status"
