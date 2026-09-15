# Protocol

Recognisable shapes and the check that identifies each.

## Shape 1 - a verify command pinned near zero CPU

`dotnet test` (or any build step) runs for tens of minutes at a fraction of a
percent of one core, produces no new output, and ends only on its command budget.

```bash
# the review command's tree, cumulative CPU seconds
ps -eo pid,ppid,etimes,times,args --forest | grep -A20 "review-<attempt>"
```

A tree whose `TIMES` column barely moves between two samples a minute apart is
blocked, not slow. Since AGT-2831 the runner reaps this itself and the report
carries `ReviewInfra/NoCpuProgress`.

## Shape 2 - a build server outliving its attempt

```bash
for pid in $(pgrep -f VBCSCompiler); do
  printf '%s cwd=%s\n' "$pid" "$(readlink /proc/$pid/cwd)"
done
ps -eo pid,args | grep -F '/nodemode:' | grep -v grep
```

A compiler server or worker node whose `cwd` is inside a review attempt
directory - or shows `(deleted)` - is serving every other attempt on the host
from a tree that no longer exists.

## Shape 3 - the rendezvous points are not under TMPDIR

```bash
ls /tmp | grep -c '^MSBuild[0-9]'          # host-global, reachable by every attempt
ls "$TMPDIR" | grep -c '^MSBuild[0-9]'     # expected: 0
scripts/review-build-server-isolation-probe.sh 4
```

This is the structural root cause and does not depend on timing. A per-attempt
`TMPDIR` cannot separate two concurrent attempts, because neither server places
its socket there.
