# Architecture

Architecture source material and decision records.

| Area | Contents |
|---|---|
| [model.md](model.md) | Marble-style architecture model contract used by drift analysis. |
| [monolith-hosted-services.md](monolith-hosted-services.md) | Every monolith hosted service classified as decide or apply-recover, its owner after AGT-3060, and the per-transition switch that selects who decides. |
| [Project Map and Quality layer concept](../../concepts/architecture-quality-layer.md) | Separates generated component discovery from authored intent and joins it to mapped guides, analysis history, and grading. |
| [project-map.md](project-map.md) | Generated, prompt-readable manifest inventory for every managed project, with per-repository provenance, internal dependencies, rough size, and dated JSON history. |
| [decisions/](decisions/README.md) | Accepted ADR archive plus proposed ADR slices. |
| [ADR-0061: orchestrator settings scope](decisions/adr-archive.md#adr-0061---orchestrator-settings-are-a-two-tier-config-project-override-wins-over-workspace-default-wins-over-platform-constant-2026-07-11) | Project override -> workspace default -> platform constant precedence, persistence, runtime wiring, and retired-modal routing. |
| [bus/](bus/) | Agent Message Bus contract and implementation state. |
| [backend-structure/](backend-structure/) | Backend feature-folder structure target and style guide. |
| [runner-lanes/](runner-lanes/) | Architecture constraints for runner and lane writers. |
| [maps/](maps/) | Sandboxed HTML maps and visual architecture pages. |
