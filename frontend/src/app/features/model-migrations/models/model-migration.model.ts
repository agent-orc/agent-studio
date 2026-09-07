/** Scope whose persisted model pin can be migrated. */
export type ModelMigrationScope = 'task' | 'pipelineStep' | 'configuration';

/**
 * One server-derived model update. The backend owns proposal detection and
 * revalidates the catalogue version plus current model before applying it.
 */
export interface ModelMigrationProposal {
  scope: ModelMigrationScope;
  projectName?: string | null;
  taskId?: string | null;
  pipelineType?: string | null;
  stepId?: string | null;
  configKey?: string | null;
  fromModel: string;
  toModel: string;
  rule: string;
  catalogVersion: string;
  explicit: boolean;
  costClassFrom?: string | null;
  costClassTo?: string | null;
  reasoningLadderFrom?: string[] | null;
  reasoningLadderTo?: string[] | null;
}

/** Response from GET /api/model-migrations. */
export interface ModelMigrationSnapshot {
  catalogVersion: string;
  catalogSource: string;
  catalogStale?: boolean;
  catalogError?: string | null;
  autoApplySafe: boolean;
  proposals: ModelMigrationProposal[];
}

/**
 * Compare-and-set request for one proposal. `toModel` and `rule` are
 * deliberately absent: the backend recomputes them from the named catalogue
 * version instead of trusting a stale browser payload.
 */
export interface ApplyModelMigrationRequest {
  scope: ModelMigrationScope;
  projectName?: string;
  taskId?: string;
  pipelineType?: string;
  stepId?: string;
  configKey?: string;
  expectedFromModel: string;
  catalogVersion: string;
}
