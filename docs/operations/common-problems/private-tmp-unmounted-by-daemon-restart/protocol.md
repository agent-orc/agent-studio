# Root-cause protocol

1. Correlate the failing run with `journalctl -u <unit> --list-boots` or
   `systemctl show <unit> --property=ActiveEnterTimestamp`. A build that fails
   within minutes of a daemon restart is suspect before anything else.
2. Read the worker's own mount table, not the daemon's:
   `sudo grep ' /tmp ' /proc/<worker-pid>/mountinfo`. A mount root ending in
   `//deleted` proves the namespace is gone.
3. Read the unit's effective setting:
   `systemctl show <unit> --property=PrivateTmp --value`. `yes` on a unit that
   also uses `KillMode=process` is the defect.
4. Classify the run before diagnosing the product. `MSB1025`,
   `SocketException (99): Cannot assign requested address`, and
   `mkdtemp ... ENOENT` are host failures. So is a verification command that
   exited non-zero with no parsed test result at all.
5. Do not respond by reverting `KillMode=process`. That trades this failure for
   killed workers and lost attempts. Remove `PrivateTmp` instead.
