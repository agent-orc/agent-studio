# Central Skills Library

Reusable specialist workflows that any CLI agent (Claude Code, Codex,
Copilot, Gemini) can pick up and follow. Architecture documented in
[`docs/concepts/skills-architecture.md`](../../docs/concepts/skills-architecture.md).

Skill format: each subfolder is one skill with a `SKILL.md` at its root that
states when to invoke, hard rules, process, and anti-patterns. Larger skills
add `scripts/`, `references/`, and `tests/` siblings.

## Active skills

| Skill | When to invoke | Folder |
|-------|----------------|--------|
| **Operator** | Operate a Studio installation with the `studio-op` CLI: watch, triage, continue, accept, routing, preflight, and the four runbooks (server-first deploy, base-verdict quarantine, stuck batch flight, lost pending push). | [`operator/`](operator/SKILL.md) |
| **Task API** | Create / move / triage tasks via HTTP. Required reading for any CLI work that touches the task queue. | [`task-api/`](task-api/SKILL.md) |
| **Regenerate README** | Rewrite `README.md` from current product reality (after a load-bearing change). | [`regenerate-readme/`](regenerate-readme/SKILL.md) |
| **Runtime log analysis** | Inspect backend / runner / CLI logs after an incident. | [`runtime-log-analysis/`](runtime-log-analysis/SKILL.md) |

## How to add a skill

1. Pick a slug (kebab-case, descriptive). Create `.agents/skills/<slug>/`.
2. Write `SKILL.md` with the standard sections (see existing skills as
   templates).
3. Add a row to the table above.
4. If the skill ships scripts: put them under `scripts/` and reference them
   from `SKILL.md`.
5. Add a pointer in [`AGENTS.md`](../../AGENTS.md) when the skill is
   load-bearing for a category of work agents already do.

## How agents find this

Two paths:

- **Orchestrator-managed runs:** the orchestrator can attach selected skills
  to a task at pickup time.
- **Direct CLI sessions** (Claude Code, Codex, Copilot, Gemini in VS Code):
  the watched project's `AGENTS.md` points to this folder. Agents read it on
  first turn and pick the relevant skills for the task.

Both paths look at this same library; there is no per-CLI fork.
