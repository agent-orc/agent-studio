#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo 'Usage: backup-encrypted.sh <data-directory> <stack-directory> <recipient-certificate> <output.cms>' >&2
  exit 2
fi
data_dir="$(realpath "$1")"
stack_dir="$(realpath "$2")"
certificate="$(realpath "$3")"
output="$4"
case "$(realpath -m "$output")" in
  "$data_dir"/*|"$stack_dir"/*) echo 'Backup must be stored outside the live data and stack directories' >&2; exit 2 ;;
esac
for item in workspace projects home caddy-data caddy-config frontend-data frontend-config origin-pull; do
  [ -d "$data_dir/$item" ] || { echo "Missing backup source: $item" >&2; exit 2; }
done
[ -f "$stack_dir/.env" ] || { echo 'Missing backup source: .env' >&2; exit 2; }
openssl x509 -in "$certificate" -noout > /dev/null
umask 077
temporary="$(mktemp "${output}.tmp.XXXXXX")"
trap 'rm -f "$temporary"' EXIT
tar -C "$data_dir" -cf - workspace projects home caddy-data caddy-config frontend-data frontend-config origin-pull \
  -C "$stack_dir" .env | openssl cms -encrypt -aes-256-gcm -binary -outform DER \
  -recip "$certificate" > "$temporary"
[ -s "$temporary" ] || { echo 'Encrypted backup is empty' >&2; exit 1; }
mv "$temporary" "$output"
echo "Encrypted backup written: $output"
