# Contracts

Durable contracts that code, CLIs, and agents must respect.

| File | Contract |
|---|---|
| [agent-task.md](agent-task.md) | App-owned task lifecycle boundary copied into watched targets. |
| [agent-contract-pattern.md](agent-contract-pattern.md) | Contract-bounded agent pattern and self-heal limits. |
| [filesystem.md](filesystem.md) | Job-folder layout, lane catalog, and on-disk shape. |
| [protocol-style.md](protocol-style.md) | `status.md`, activity markers, attachments, results, and image retention. |
| [run-outcome.md](run-outcome.md) | Post-run classification shared by lane routing and UI surfacing. |
| [remote-run-result.md](remote-run-result.md) | Immutable infrastructure scenario result assembled from authoritative Task Server and Runner evidence. |
| [ui-task-pipeline.md](ui-task-pipeline.md) | Iterative UI routing, per-iteration visual evidence, cap, and Part 2 Human Gate marker. |
| [loop-inventory.md](loop-inventory.md) | Re-entry, retry, and loop breaker registry. |
| [code-patterns.md](code-patterns.md) | Code-pattern drift watchlist read by analysis services. |
| [runtime-prompts.md](runtime-prompts.md) | Runtime prompt precedence, review companions, project override comparison, call telemetry, and cost boundaries. |
| [wiki-tree.md](wiki-tree.md) | Wiki physical docs tree, mutation, rendering, and history contract. |
| [git-state-index.md](git-state-index.md) | Git-derived board state as a background index; request paths never spawn or wait on git. |
