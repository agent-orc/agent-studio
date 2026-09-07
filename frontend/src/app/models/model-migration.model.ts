/**
 * Backend-owned proposal for moving one explicit model pin to a newer model.
 * The Token Economy catalogue is the authority for safety and impact; the
 * frontend only presents this projection and sends the chosen target back.
 */
export interface ModelMigrationProposal {
  from: string;
  to: string;
  family: string;
  rule: string;
  catalogVersion: string;
  safeAuto: boolean;
  targetAvailable: boolean | null;
  safeAutoCandidate: boolean;
  costClassFrom: string;
  costClassTo: string;
  fromReasoningLevels: string[];
  toReasoningLevels: string[];
  ladderCompatible: boolean;
  note: string;
  vendor?: string;
  contextChange?: string;
}

/** One configurable backend pin surfaced on the CLI Management page. */
export interface ModelConfigurationPin {
  id: string;
  label: string;
  configKey: string;
  currentModel: string;
  modelMigration: ModelMigrationProposal | null;
}
