# Ideas

Hypotheses, open questions, ruled-out approaches.

- **Open: the first sweep after a HEAD move is still unbounded in the working
  tree.** `git ls-files --cached --others --exclude-standard` walks the whole
  tree, and the commit window bounds only `git log`. A repository with a very
  large untracked tree still pays. A candidate is dropping `--others` and
  indexing tracked files only, at the cost of not finding new files before they
  are added.
- **Open: warm the index off the request path.** HEAD changes are already
  observable; a background warmer could rebuild a repository's index when its
  HEAD moves, so the first palette query after a commit is warm too. Not done
  under AGT-2723 because it puts git spawns on a timer rather than on demand,
  which needs its own budget.
- **Open: dossiers as a fourth domain.** The stream frame vocabulary already
  carries a `domain` string, so a `dossiers` chunk slots in without a wire
  change. Tracked as AGT-2722.
- **Ruled out: a persisted index on disk.** The whole corpus rebuilds in well
  under a second from a warm page cache, and a persisted index adds an
  invalidation problem worse than the one it solves.
- **Ruled out: storing a lowercased copy of every card body for matching.**
  Doubles the resident corpus for a gain that `OrdinalIgnoreCase` matching
  already provides. Only the small identifier header (key, title, state,
  project) is pre-lowercased, because that is what almost every palette query
  actually hits.
- **Ruled out: four parallel domain endpoints instead of one stream.** Four
  requests cannot express "repository 7 of 15" without either a shared server
  side session or the client guessing. The SSE `progress` frame is the whole
  point.
- **Considered: raising `CommitWindow` above 2,000.** It is a first-sweep cost,
  not a per-query cost, so raising it is cheaper than it looks. Left at 2,000
  until someone reports a real miss beyond it.
