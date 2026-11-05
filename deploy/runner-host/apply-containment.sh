#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
unit_dir=/etc/systemd/system
units=(studio.slice agent-work.slice)
services=(agent-runner.service agent-runner-review.service agent-host.service)
dropin=50-runner-containment.conf

usage() {
  echo 'Usage: apply-containment.sh [--remove | --check | --render-slices | --calculate <cores> <MemTotal-kB>] [--agent-cpu-weight <1..10000>] [--studio-cpu-weight <1..10000>] [--agent-io-weight <1..10000>] [--studio-io-weight <1..10000>]' >&2
  exit 2
}

agent_cpu_weight=50
studio_cpu_weight=100
agent_io_weight=50
studio_io_weight=100
mode=apply
calculate_cores=
calculate_mem_kib=
while (($#)); do
  case "$1" in
    --remove|--check|--render-slices)
      [[ $mode == apply ]] || usage
      mode=${1#--}; shift ;;
    --calculate)
      [[ $mode == apply && $# -ge 3 ]] || usage
      mode=calculate; calculate_cores=$2; calculate_mem_kib=$3; shift 3 ;;
    --agent-cpu-weight|--studio-cpu-weight|--agent-io-weight|--studio-io-weight)
      (($# >= 2)) || usage
      case "$1" in
        --agent-cpu-weight) agent_cpu_weight=$2 ;;
        --studio-cpu-weight) studio_cpu_weight=$2 ;;
        --agent-io-weight) agent_io_weight=$2 ;;
        --studio-io-weight) studio_io_weight=$2 ;;
      esac
      shift 2 ;;
    *) usage ;;
  esac
done
for weight in "$agent_cpu_weight" "$studio_cpu_weight" "$agent_io_weight" "$studio_io_weight"; do
  [[ $weight =~ ^[1-9][0-9]*$ ]] && ((weight <= 10000)) || usage
done
((agent_cpu_weight < studio_cpu_weight)) || {
  echo 'Agent CPU weight must be lower than Studio CPU weight.' >&2
  exit 2
}

render_slice() {
  local unit=$1 cpu_weight=$2 io_weight=$3
  sed -e "s/^CPUWeight=.*/CPUWeight=$cpu_weight/" \
      -e "s/^IOWeight=.*/IOWeight=$io_weight/" "$script_dir/systemd/$unit"
}

# Keep two logical cores and up to 10 GiB for Studio on a large host. On a
# smaller host, keep one core and a quarter of RAM. MemoryHigh leaves an
# additional reclaim margin below the aggregate hard limit.
calculate() {
  local cores=$1 mem_kib=$2 total_mib reserve_mib work_max_mib work_high_mib work_cores
  [[ $cores =~ ^[1-9][0-9]*$ && $mem_kib =~ ^[1-9][0-9]*$ ]] || usage
  total_mib=$((mem_kib / 1024))
  (( total_mib >= 2048 )) || { echo 'At least 2 GiB RAM is required.' >&2; exit 2; }
  reserve_mib=$((total_mib / 4))
  (( reserve_mib > 10240 )) && reserve_mib=10240
  work_max_mib=$((total_mib - reserve_mib))
  work_high_mib=$((work_max_mib * 9 / 10))
  work_cores=$((cores - 2))
  (( work_cores < 1 )) && work_cores=1
  printf 'CPUQuota=%s%%\nMemoryLow=%sM\nMemoryHigh=%sM\nMemoryMax=%sM\n' \
    "$((work_cores * 100))" "$reserve_mib" "$work_high_mib" "$work_max_mib"
}

if [[ $mode == calculate ]]; then
  calculate "$calculate_cores" "$calculate_mem_kib"
  exit 0
fi
if [[ $mode == render-slices ]]; then
  for unit in "${units[@]}"; do
    echo "# $unit"
    if [[ $unit == studio.slice ]]; then
      render_slice "$unit" "$studio_cpu_weight" "$studio_io_weight"
    else
      render_slice "$unit" "$agent_cpu_weight" "$agent_io_weight"
    fi
  done
  exit 0
fi

show_state() {
  local unit
  for unit in "${units[@]}" "${services[@]}"; do
    echo "$unit:"
    systemctl show "$unit" -p LoadState -p ActiveState -p Slice -p ControlGroup \
      -p CPUWeight -p IOWeight -p CPUQuotaPerSecUSec -p MemoryLow -p MemoryHigh -p MemoryMax \
      --no-pager || true
  done
}

if [[ $mode == check ]]; then
  show_state
  for unit in "${services[@]}"; do
    slice=$(systemctl show "$unit" --property=Slice --value 2>/dev/null || true)
    group=$(systemctl show "$unit" --property=ControlGroup --value 2>/dev/null || true)
    printf '%s placement: slice=%s cgroup=%s\n' "$unit" "${slice:-unknown}" "${group:-unknown}"
  done
  command -v systemd-cgls >/dev/null && systemd-cgls --unit agent-work.slice --no-pager || true
  exit 0
fi
(( EUID == 0 )) || { echo 'Run as root (sudo).' >&2; exit 1; }
[[ -f /sys/fs/cgroup/cgroup.controllers ]] || { echo 'cgroup v2 is required.' >&2; exit 1; }
command -v systemctl >/dev/null || { echo 'systemctl is required.' >&2; exit 1; }

echo 'Before:'
show_state

if [[ $mode == remove ]]; then
  # Reset active limits before removing the persistent definitions. Running
  # processes remain in their old cgroups until they are restarted.
  if systemctl is-active --quiet studio.slice; then
    systemctl set-property --runtime studio.slice MemoryLow=0
  fi
  if systemctl is-active --quiet agent-work.slice; then
    systemctl set-property --runtime agent-work.slice CPUQuota= MemoryHigh=infinity MemoryMax=infinity
  fi
  # revert clears the persistent set-property drop-ins owned by these slices.
  if [[ -e $unit_dir/studio.slice || -e $unit_dir/agent-work.slice \
    || -d /etc/systemd/system.control/studio.slice.d \
    || -d /etc/systemd/system.control/agent-work.slice.d ]]; then
    systemctl revert "${units[@]}"
  fi
  for unit in "${services[@]}"; do
    rm -f "$unit_dir/$unit.d/$dropin"
    rmdir "$unit_dir/$unit.d" 2>/dev/null || true
  done
  for unit in "${units[@]}"; do
    rm -f "$unit_dir/$unit"
  done
  systemctl daemon-reload
  echo 'After removal:'
  show_state
  echo 'Restart agent-runner.service, replace Review through agent-runner-deploy restart-review, and recreate the Studio and engine containers to leave their current slices.'
  exit 0
fi

command -v nproc >/dev/null || { echo 'nproc is required.' >&2; exit 1; }
cores=$(nproc)
mem_kib=$(awk '/^MemTotal:/ { print $2; exit }' /proc/meminfo)
values=$(calculate "$cores" "$mem_kib")
echo "Host: $cores logical cores, $mem_kib kB MemTotal"
echo "$values"

# Compose's cgroup_parent is a systemd slice name only with this driver.
command -v docker >/dev/null || { echo 'Docker is required.' >&2; exit 1; }
if [[ $(docker info --format '{{.CgroupDriver}}') != systemd ]]; then
  echo 'Docker must use the systemd cgroup driver.' >&2
  exit 1
fi

render_slice studio.slice "$studio_cpu_weight" "$studio_io_weight" | install -m 0644 /dev/stdin "$unit_dir/studio.slice"
render_slice agent-work.slice "$agent_cpu_weight" "$agent_io_weight" | install -m 0644 /dev/stdin "$unit_dir/agent-work.slice"
for unit in "${services[@]}"; do
  install -D -m 0644 "$script_dir/systemd/$unit.d/$dropin" "$unit_dir/$unit.d/$dropin"
done
systemctl daemon-reload
systemctl start "${units[@]}"

quota=$(sed -n 's/^CPUQuota=//p' <<<"$values")
low=$(sed -n 's/^MemoryLow=//p' <<<"$values")
high=$(sed -n 's/^MemoryHigh=//p' <<<"$values")
max=$(sed -n 's/^MemoryMax=//p' <<<"$values")
systemctl set-property studio.slice "MemoryLow=$low"
systemctl set-property agent-work.slice "CPUQuota=$quota" "MemoryHigh=$high" "MemoryMax=$max"

echo 'After:'
show_state
echo 'Restart agent-runner.service and replace Review through agent-runner-deploy restart-review to move them into agent-work.slice.'
echo 'Recreate the Studio and engine containers with their runner-host Compose settings to enter studio.slice.'
echo 'Existing delegated worker cgroups stay where they are until their work completes; drain work before restarting the runner services.'
