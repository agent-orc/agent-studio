# System

## Zweck & Abgrenzung

Das System-of-Record: die verbindlichen, aktuellen Verträge und Karten, gegen die
Code, Reviews und Drift-Analysen prüfen.

**Gehört hierher:** Architekturmodell und ADR-Archiv, Domänenkarten (Runner,
Pipeline, Tasks, Frontend, CLI, Tokens), durable Contracts (Filesystem, Task,
Protokoll, Run-Outcome, Wiki-Tree), CLI-Vertrag und Report-Verträge.

**Gehört nicht hierher:** erklärende oder noch reifende Konzepte (→ `concepts/`),
Betriebs-/Setup-Wissen (→ `operations/`), Style-/Design-Guides (→ `quality/`).
Maschinen-Verträge in Datei-Form (JSON-Schemas, Config, In-App-Hilfe) liegen unter
`app/` und ändern Pfad und Format nur zusammen mit Code.

## Areas

| Area | Contents |
|---|---|
| [architecture/](architecture/README.md) | Architecture model, ADR archive, proposed ADRs, backend structure, bus docs, runner-lane constraints, and HTML maps. |
| [domains/](domains/README.md) | Current system-of-record domain maps for runner, pipeline, tasks, frontend, CLI, and tokens. |
| [contracts/](contracts/README.md) | Durable filesystem, task, protocol, run-outcome, code-pattern, and wiki-organization contracts. |
| [reports/](reports/README.md) | Report contracts and screenshot-backed visual documentation. |
| [cli/](cli/README.md) | Supported CLI contract, per-CLI skills, audits, and investigations. |
| [common-problems/](common-problems/) | Recurring system-level problems with root-cause analysis and occurrence logs. |
| [ui-standard/](ui-standard/README.md) | Pilot pages of the UI standard: patterns with screenshot and rule, and the shared components behind them. |
