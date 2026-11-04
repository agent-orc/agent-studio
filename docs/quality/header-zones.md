---
styleGuideId: header-zones
title: Studio header zones
version: 1
summary: Slot ownership, relevance, treatment, and responsive rules for the app and task headers.
promptSummary: Follow docs/quality/frontend/style-guide/header.md for every Studio app or task header change. Keep the registered slot order and one owner for each fact. Render a task block only when it says something useful in the current lane and phase; omit empty, not-applicable, and speculative loading blocks. Use the Deck-Panel treatment and shared button vocabulary; do not nest panel shells or add a colored left edge. Keep the tab row and toolbar usable at 1024 px and verify both themes. Register each new task header child and extend the guide.
appliesTo: {"projects":["*"],"technologies":["angular"],"taskAreas":["header"]}
---

# Studio header zones

The binding implementation rules and slot inventory live in the
[header style guide](frontend/style-guide/header.md). Read it before changing
the app header, task tab strip, task toolbar, or any block above the task panes.
