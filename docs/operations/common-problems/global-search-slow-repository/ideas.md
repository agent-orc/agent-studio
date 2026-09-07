# Ideas

- Persist the file and commit index across restarts so the first query after a
  backend start is warm too, not only the first query after a HEAD move.
- Warm the git indexes from the same signal that already notices a HEAD change,
  so the walk happens off the request path like the task blob does.
- Report the per-repository index age in the palette when a repository is
  measurably slower than its peers, so the operator can see which checkout costs.
