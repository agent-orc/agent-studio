# Root-Cause Protocol

1. Read `global-search-completed`. Compare `taskMs` with `gitMs`. A small
   `taskMs` and a large `gitMs` puts the cost in the git domains, not the cards.
2. Read the `cache=` field. Each entry is `<repository>=<commits>/<files>` with
   `hit`, `miss`, or `skipped`. Repeated `miss` for the same repository across
   consecutive queries means its HEAD is moving, not that the cache is broken.
3. Read `global-search-slow` when present: it names the slowest repository and
   its duration.
4. For that repository, time the two index commands by hand:
   `git ls-files --cached --others --exclude-standard | wc -l` and
   `git log --all --no-merges --max-count=2000 --pretty=format:%H | wc -l`.
   A large first number usually means `.gitignore` is not covering build output.
