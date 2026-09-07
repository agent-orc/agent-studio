---
id: global-search-slow-repository
title: "Global search is slow because one repository has a long history"
status: mitigated
first-seen: 2026-09-06T00:00:00Z
last-seen: 2026-09-06T00:00:00Z
severity: major
category: ui
tags: [global-search, git, index, latency, palette]
affects:
  - "Ctrl+K command palette"
  - "GET /api/search"
  - "GET /api/search/stream"
related-tasks: [AGT-2723]
related-adrs: []
---

# Global search is slow because one repository has a long history

**What.** The palette takes seconds to fill and the backend logs a
`global-search-slow` warning naming one repository. Every other project answers
quickly.

**Why.** The commit domain builds its index from a bounded `git log` window per
repository per HEAD. On a repository with a very long history that first build
after a push is the dominant cost. The result is cached against HEAD, so only
the first search after each push pays it.

**Workaround.** None needed for correctness: the stream delivers task matches
immediately and each repository appends as it finishes, so the slow checkout no
longer blocks the rest. The domain row names which repository is still running.

**Long-term.** Lower `Search:CommitWindow` (default 2000, clamped to
100-20000) for a deployment whose largest repository makes the first build too
expensive. Trading commit recall for a smaller log window is the intended
control; see the search paragraph in
[docs/system/domains/frontend.md](../../domains/frontend.md#global-search).
