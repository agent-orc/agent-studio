---
id: slow-global-search-on-large-repository
title: "Global search stalls on a repository with a long history"
status: mitigated
first-seen: 2026-09-06T18:00:00Z
last-seen: 2026-09-06T21:00:00Z
severity: major
category: frontend
tags: [global-search, palette, git, cache, latency, index]
affects: [backend/Features/Search/GlobalSearchService.cs, backend/Features/Search/TaskSearchIndex.cs, frontend/src/app/features/studio-shell/components/global-search]
related-tasks: [AGT-2723]
related-adrs: []
---

# slow-global-search-on-large-repository

**What.** The Ctrl+K palette takes seconds to return, and while it does the
operator sees a single spinner with nothing moving. Measured on 2026-09-06:
`AGT-W15` 17.2 s, `quota` 19.6 s, `runner` 22.0 s, with `global-search-completed`
durations across one evening ranging from 1.0 s to 36.3 s. The palette could not
say whether it was working, which repository it was on, or whether it had hung.

**Why.** Two independent causes, either sufficient on its own.

1. **Per-query git spawns.** Commit and file lookup were memoized under a key
   that included the query string, so every new search term re-ran
   `git log` and `git ls-files` across roughly fifteen checkouts, sequentially.
   Nothing was returned until the last repository answered.
2. **Per-query file reads.** The task domain re-read `prompt.md` and `status.md`
   for every card, live and archived, on every query. At 963 cards that is
   roughly 2,000 file probes per keystroke-driven search.

The visible symptom - one spinner for the whole duration - was a third,
independent problem: the palette had a single `loading` flag with no debounce,
no cancellation, and no per-domain state.

**Mitigation (AGT-2723).** Cache keys carry the repository, the domain, and
HEAD, but **not** the query, so matching is an in-memory pass and a second term
against an unchanged HEAD spawns no git process. The commit window is bounded to
2,000 commits per repository. The task domain reads `TaskSearchIndex`, an
in-memory full-text index versioned by `TaskIndexCache.Generation` and rebuilt in
the background. Repositories are swept in parallel with a bounded degree and
delivered per domain over `GET /api/search/stream`.

**What is still slow, and what to do about it.** The *first* sweep of a
repository after its HEAD moves still pays one `git ls-files` and one bounded
`git log`. On a repository with a very long history or a very large working
tree, that first sweep can still take seconds. It no longer blocks the task
domain, and it no longer repeats per keystroke, but it is the remaining floor.

Operator-visible signals and knobs are in [measures.md](./measures.md).
