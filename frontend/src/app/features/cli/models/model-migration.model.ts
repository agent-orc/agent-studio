/**
 * AGT-2716 — model-family migration catalog served by
 * `GET /api/cli/model-migrations`. Lists safe (and, in future, non-safe)
 * upgrades from a superseded model id to its current generation, e.g.
 * `claude-opus-4-8 -> claude-opus-5`. The orchestrator auto-applies
 * `safeAuto` entries for non-explicit task models at run admission; explicit
 * pins (card model, pipeline-step override) only ever see an offer through
 * this catalog — the backend never rewrites them silently.
 *
 * The catalog is intentionally data-driven: the frontend must never hardcode
 * a specific `from`/`to` id and should always resolve proposals by looking
 * them up here.
 */
export interface ModelMigrationEntry {
  from: string;
  to: string;
  family: string;
  safeAuto: boolean;
  reason: string;
}

export interface ModelMigrationCatalog {
  version: string;
  wikiPath: string;
  migrations: ModelMigrationEntry[];
}
