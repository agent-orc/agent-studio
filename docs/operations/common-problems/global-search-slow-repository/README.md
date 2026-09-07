---
id: global-search-slow-repository
title: "Global search stalls because one repository has a very large history or working tree"
status: mitigated
first-seen: 2026-09-06T18:00:00Z
last-seen: 2026-09-06T22:00:00Z
severity: major
category: ui
tags: [global-search, palette, git, performance, cache, head-keyed, index]
affects:
  - "Ctrl+K command palette"
  - "backend/Features/Search/GlobalSearchIndex.cs"
  - "GET /api/search, GET /api/search/stream"
related-tasks: [AGT-2723]
related-adrs: []
---

# global-search-slow-repository

**What.** A palette query takes seconds instead of milliseconds. The
`global-search-slow` warning names the repository that dominated the search, and
`global-search-completed` shows a large `gitMs` next to a small `taskMs`.

**Why.** The git domains cost one `git ls-files` and one `git log` per
repository per revision. Both are bounded, but the bound is per repository: a
checkout with a very deep history or a very large untracked working tree still
pays a real walk on the first query after its HEAD moves. With around fifteen
registered repositories, a single slow one sets the wall clock for the whole
git portion.

Historically this was much worse and for a different reason: the HEAD-keyed
cache key embedded the query string, so every keystroke re-ran both commands
across every repository and the LRU thrashed. That is fixed - the key now
carries the repository root only.

**Workaround.** None is needed for correctness. Task results answer from the
warm in-memory index within a frame and are never blocked by git; the streamed
endpoint delivers each repository as it finishes, so a slow repository degrades
that one row rather than the whole palette. Press Escape to stop a search you no
longer want; the backend kills the git children still walking.

**Long-term.** The commit index is a bounded window
(`GlobalSearchIndex.CommitWindow`, the newest 2,000 commits) rather than the
full history, and the file index is capped at `FileWindow` paths with a
`global-search-file-index-truncated` warning when it trims. If one repository is
persistently slow, reduce what it exposes rather than raising the windows:
confirm its `.gitignore` covers build output so `git ls-files --others
--exclude-standard` is not walking a large untracked tree, and consider a
shallow or pruned checkout for a repository whose deep history nobody searches.
