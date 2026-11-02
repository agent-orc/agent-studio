/** CLI feature public API. Cycle 9h / ADR-0034. */
export { CliAdminPanelComponent } from './components/cli-admin-panel/cli-admin-panel';
export { CliConsoleComponent } from './components/cli-console/cli-console';
export { CliModelsPanelComponent } from './components/cli-models-panel/cli-models-panel';
export { CliContractsPanelComponent } from './components/cli-contracts-panel/cli-contracts-panel';
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
