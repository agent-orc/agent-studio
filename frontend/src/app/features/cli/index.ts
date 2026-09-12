/** CLI feature public API. Cycle 9h / ADR-0034. */
export { CliAdminPanelComponent } from './components/cli-admin-panel/cli-admin-panel';
export { CliConsoleComponent } from './components/cli-console/cli-console';
export { CliSessionsPanelComponent } from './components/cli-sessions-panel/cli-sessions-panel';
export { CliPathsPanelComponent } from './components/cli-paths-panel/cli-paths-panel';
export { CliModelsPanelComponent } from './components/cli-models-panel/cli-models-panel';
export { CliContractsPanelComponent } from './components/cli-contracts-panel/cli-contracts-panel';
export { CliWorkingMemoryPanelComponent } from './components/cli-working-memory-panel/cli-working-memory-panel';
export type {
  CliModelInfo,
  CliModelCatalog,
  CliCompletionContract,
  CliSessionInfo,
  CliUsageProjectGroup,
  CliUsageSection,
  CliUsageReport,
  CliSessionDetail,
  CliSessionDeleteResult,
  LinkedJobRef,
  CliWorkingMemoryEntry,
  CliWorkingMemoryReport,
  CliWorkingMemoryDeleteResult,
} from './models/cli.model';
export { CLAUDE_FALLBACK_MODEL_ID, MODEL_IDS } from './models/model-ids';
export { orderModelCatalog } from './models/model-catalog-ordering';
export { CliCatalogStore } from './services/cli-catalog.store';
export type { ModelMigrationCatalog, ModelMigrationEntry } from './model-migrations';
export { ModelMigrationCatalogStore } from './model-migrations';
