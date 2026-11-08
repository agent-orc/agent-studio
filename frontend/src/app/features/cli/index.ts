/** CLI feature public API. Cycle 9h / ADR-0034. */
export type {
  CliModelInfo,
  CliModelCatalog,
  CliCompletionContract,
  CliSessionInfo,
  CliUsageProjectGroup,
  CliUsageSection,
  CliUsageReport,
  LinkedJobRef,
} from './models/cli.model';
export { CLAUDE_FALLBACK_MODEL_ID, MODEL_IDS } from './models/model-ids';
export { orderModelCatalog } from './models/model-catalog-ordering';
export { CliCatalogStore } from './services/cli-catalog.store';
export type { ModelMigrationCatalog, ModelMigrationEntry } from './model-migrations';
export { ModelMigrationCatalogStore } from './model-migrations';
