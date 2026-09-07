# Workspace CLI Management

Status: Current

Owner: CLI and model-routing domains

Workspace CLI Management is the operator view for installed CLI model
availability, family-owned defaults, and Token Economy migration policy. It
keeps two decisions distinct:

1. Which concrete model a family-owned default resolves to now.
2. Whether a persisted concrete-model pin has a reviewed migration available.

The canonical routing and correctness rules remain in the
[model routing policy](../system/domains/model-routing-policy.md). This page
explains how an operator observes and controls those rules.

## Sources and responsibilities

| Source | Responsibility | Failure behavior |
|---|---|---|
| Live Claude and Codex catalogs | Report models available in the installed CLI. | A stale or failed discovery is not used as fresh availability evidence. |
| `ModelMetadataRegistry` | Assign family and generation order, and provide the resolver fallback. | Resolution fails if a family has no available registry member. |
| Token Economy `model-migrations.v1.json` | Version migration targets, cost classes, reasoning ladders, and `safeAuto` evidence. | First failure disables proposals and automation; later failures retain the last-good catalog as stale. |
| Workspace settings | Store whether safe automatic migrations are allowed. | `autoApplySafeModelMigrations` defaults to enabled and can be disabled by the operator. |

The Token Economy catalog remains project-owned data. Studio locates the
registered Token Economy repository and reads
`src/TokenEconomy/catalog/model-migrations.v1.json` through a bounded cache.
The CLI Management view shows the catalog version and whether the current
snapshot is fresh, stale, or unavailable.

## Family-owned defaults

Studio uses five stable family references: `claude-haiku`, `claude-sonnet`,
`claude-opus`, `gpt-mini`, and `gpt-flagship`. A fresh live catalog is filtered
to available, non-deprecated members of the requested family and ordered by the
registry generation value. A stale, absent, or incomplete live catalog falls
back to the newest available registry member.

This makes a default follow a newly installed generation without redefining its
role. It does not allow an economical Haiku role to jump to Sonnet, or a Mini
supporting role to jump to a flagship model. A family change is a Token Economy
and correctness-policy decision.

## Migration ownership

| Current model ownership | Visible update proposal | Eligible for `safeAuto` at admission |
|---|---:|---:|
| Family-owned supporting-agent default | When a concrete retained value is superseded | Yes |
| Non-explicit task/default selection | When the catalog has a newer target | Yes |
| Explicit task card model | Yes | No |
| Project pipeline-step override | Yes | No |
| Workspace configuration pin | Yes | No |

An explicit pin is never rewritten silently. Its update offer shows the source
and target model plus the cost-class and reasoning-ladder difference. Selecting
Apply is the consent boundary. The backend then performs a compare-and-set
check against the expected source model and recomputes the proposal from the
named catalog version.

For non-explicit ownership, run admission may apply a rule only when Token
Economy marks it `safeAuto` and the runtime checks still pass. Those checks
include same-family progression, a newer generation, same or lower known cost
class, ladder compatibility, supporting evidence, and current target
availability. Model-routing correctness floors continue to win over economy or
quota pressure.

## Operator workflow

1. Open Workspace CLI Management and inspect the installed catalog and Token
   Economy catalog version.
2. Review configuration proposals in this view. Task cards and project
   pipeline settings show proposals for their own persisted pins.
3. Compare the cost class and reasoning ladder before applying a proposal.
4. Use the automatic-application switch to permit or disable `safeAuto` rules
   for non-explicit selections. Disabling it does not hide manual proposals.
5. Confirm automatic changes in the task timeline and operator feed.

The migration endpoints are deliberately small:

| Method and route | Purpose |
|---|---|
| `GET /api/model-migrations` | Read catalog state, workspace switch state, and scoped proposals. |
| `POST /api/model-migrations/apply` | Apply one revalidated proposal using compare-and-set identity. |
| `PUT /api/model-migrations/auto-apply` | Enable or disable safe automatic migrations for the workspace. |

## Audit and recovery

Each automatic application writes a `model_migrated` task timeline event. Its
details contain `from`, `to`, `rule`, and `catalogVersion`. The same decision is
also written as an operator-feed action. Together they answer which value
changed, which rule allowed it, and which evidence version was active.

An unavailable first catalog load is a safe no-op: there are no proposals and
no automatic writes. After at least one successful load, a refresh failure
keeps the last-good snapshot for visibility but marks it stale and exposes the
load error. Operators should restore the registered Token Economy checkout or
its catalog before treating new migrations as current. Live CLI availability
remains an independent gate even when a last-good catalog is available.

## Related contracts

- [Model routing policy](../system/domains/model-routing-policy.md)
- [CLI domain](../system/domains/cli.md)
- [Pipeline domain](../system/domains/pipeline.md)
- [Workspace settings model](../../backend/Shared/Models/WorkspaceSettings.cs)

## Knowledge log

### 2026-09-07 - Family defaults and audited migration control

AGT-2716 introduced five stable family references, live-catalog resolution with
a registry fallback, Token Economy migration proposals, an operator-controlled
`safeAuto` gate, and timeline plus operator-feed audit records. Explicit task,
pipeline, and configuration pins remain manual update decisions.
