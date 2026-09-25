#!/usr/bin/env bash
# Clean-machine acceptance harness for the default Docker Compose onboarding path.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixture_root="$repo_root/scripts/fixtures/compose-smoke-cloud-init"
cache_root="${COMPOSE_SMOKE_VM_CACHE:-/var/tmp/agent-studio-compose-smoke-cache}"
artifact_root="${COMPOSE_SMOKE_VM_ARTIFACTS:-${JOB_RESULTS_DIR:-$repo_root/results}/compose-smoke-vm}"
memory_mb="${COMPOSE_SMOKE_VM_MEMORY_MB:-8192}"
cpus="${COMPOSE_SMOKE_VM_CPUS:-4}"
timeout_seconds="${COMPOSE_SMOKE_VM_TIMEOUT_SECONDS:-3600}"
image_release="20260615"
image_name="ubuntu-24.04-server-cloudimg-amd64.img"
image_url="https://cloud-images.ubuntu.com/releases/noble/release-${image_release}/${image_name}"
image_sha256="5fa5b05e5ec239858c4531485d6023b0896448c2df7c63b34f8dae6ea6051a44"
base_image="$cache_root/$image_release-$image_name"
run_root="$(mktemp -d /var/tmp/agent-studio-compose-smoke-vm.XXXXXX)"
serial_log="$artifact_root/serial.log"

cleanup()
{
    status="$?"
    trap - EXIT
    if [ "${COMPOSE_SMOKE_VM_KEEP_RUN_DIR:-0}" = "1" ]; then
        printf 'compose-smoke-vm-run-dir=%s\n' "$run_root"
    else
        rm -rf -- "$run_root"
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

required_commands=(
    cloud-localds
    curl
    genisoimage
    qemu-img
    qemu-system-x86_64
    sha256sum
    tar
    timeout
)
for command_name in "${required_commands[@]}"; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'missing required command: %s\n' "$command_name" >&2
        exit 2
    }
done

if [ ! -c /dev/kvm ] || [ ! -r /dev/kvm ] || [ ! -w /dev/kvm ]; then
    printf '%s\n' \
        'KVM is unavailable to this user.' \
        'Enable nested virtualization and grant read/write access to /dev/kvm.' >&2
    exit 2
fi

mkdir -p "$cache_root" "$artifact_root"

if ! printf '%s  %s\n' "$image_sha256" "$base_image" | sha256sum --check --status; then
    download_path="$run_root/$image_name"
    curl --fail --location --show-error --output "$download_path" "$image_url"
    printf '%s  %s\n' "$image_sha256" "$download_path" | sha256sum --check --status
    mv "$download_path" "$base_image"
fi

source_archive="$run_root/agent-studio-source.tar.gz"
tar \
    --create \
    --gzip \
    --file "$source_archive" \
    --directory "$repo_root" \
    --exclude-vcs \
    --exclude='*/.angular' \
    --exclude='*/bin' \
    --exclude='*/dist' \
    --exclude='*/node_modules' \
    --exclude='*/obj' \
    --exclude='*/appsettings.Development.json' \
    --exclude='*/appsettings.Local.json' \
    --exclude='*/runner.env' \
    --exclude='*/runner.token' \
    --exclude='./artifacts' \
    --exclude='./results' \
    --exclude='./test-results' \
    .

genisoimage \
    -quiet \
    -J \
    -R \
    -V AGT2305SRC \
    -o "$run_root/source.iso" \
    -graft-points \
    "agent-studio-source.tar.gz=$source_archive"

cloud-localds \
    "$run_root/seed.iso" \
    "$fixture_root/user-data" \
    "$fixture_root/meta-data"

qemu-img create \
    -q \
    -f qcow2 \
    -F qcow2 \
    -b "$base_image" \
    "$run_root/guest.qcow2" \
    24G

set +e
timeout \
    --signal=TERM \
    --kill-after=30s \
    "$timeout_seconds" \
    qemu-system-x86_64 \
        -name agent-studio-compose-smoke \
        -machine accel=kvm \
        -cpu host \
        -smp "$cpus" \
        -m "$memory_mb" \
        -nographic \
        -no-reboot \
        -nic user,model=virtio-net-pci \
        -drive "file=$run_root/guest.qcow2,if=virtio,format=qcow2" \
        -drive "file=$run_root/seed.iso,if=virtio,format=raw,readonly=on" \
        -drive "file=$run_root/source.iso,media=cdrom,format=raw,readonly=on" \
    2>&1 | tee "$serial_log"
qemu_status="${PIPESTATUS[0]}"
set -e

if [ "$qemu_status" -ne 0 ]; then
    printf 'QEMU exited with status %s; serial log: %s\n' \
        "$qemu_status" "$serial_log" >&2
    exit "$qemu_status"
fi

grep -Fq 'AGT2305_VM_EXIT=0' "$serial_log"
grep -Fq 'AGT2305_VM_RESULT=passed' "$serial_log"

printf '%s\n' \
    "compose-smoke-vm=passed" \
    "ubuntu-image-release=$image_release" \
    "ubuntu-image-sha256=$image_sha256" \
    "memory-mb=$memory_mb" \
    "cpus=$cpus" \
    "serial-log=$serial_log"
