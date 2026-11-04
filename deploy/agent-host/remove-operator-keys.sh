#!/bin/sh
# Remove only reviewed key fingerprints from the authorized_keys files named by the host administrator.
set -eu
if [ "$(id -u)" -ne 0 ]; then echo 'root-required' >&2; exit 1; fi
if [ "$#" -lt 2 ] || [ ! -f "$1" ]; then
  echo 'usage: remove-operator-keys.sh <fingerprints-file> <authorized_keys-file>...' >&2
  exit 2
fi
fingerprints=$1
shift
umask 077
line_file=$(mktemp)
trap 'rm -f "$line_file"' EXIT HUP INT TERM
for keys_file in "$@"; do
  if [ ! -f "$keys_file" ] || [ -L "$keys_file" ]; then echo 'authorized-keys-file-invalid' >&2; exit 1; fi
  replacement=$(mktemp "${keys_file}.XXXXXX")
  chown --reference="$keys_file" "$replacement"
  chmod --reference="$keys_file" "$replacement"
  removed=0
  while IFS= read -r line || [ -n "$line" ]; do
    printf '%s\n' "$line" > "$line_file"
    fingerprint=$(ssh-keygen -lf "$line_file" 2>/dev/null | awk 'NR == 1 { print $2 }')
    if [ -n "$fingerprint" ] && grep -Fxq "$fingerprint" "$fingerprints"; then
      removed=$((removed + 1))
    else
      printf '%s\n' "$line" >> "$replacement"
    fi
  done < "$keys_file"
  mv -f "$replacement" "$keys_file"
  echo "removed=$removed file=$keys_file"
done
