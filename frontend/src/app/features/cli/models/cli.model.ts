/**
 * Cycle 9 CLI catalog + usage models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * Covers the model catalog returned by `/api/cli/{type}/models` and the
 * cross-CLI usage report from `/api/cli/usage`. Job-coupled CLI types
 * (CliExecution, CliSettings, ContinueMode, ContextUsageSnapshot) stay
 * in job.model.ts because they participate in the TaskInfo graph.
 */

import type { CliType, SessionUsage } from '../../../models/task.model';

export interface CliModelInfo {
  id: string;
  label: string;
  multiplier: number | null;
  vendor: string | null;
  isDefault: boolean;
  thinkingLevels?: string[];
  defaultThinkingLevel?: string | null;
  available?: boolean;
  deprecated?: boolean;
  /** Client-side catalog projection used to group an earlier generation without deprecating it. */
  olderGeneration?: boolean;
  availabilityNote?: string | null;
}

export interface CliModelCatalog {
  models: CliModelInfo[];
  source: string;
  fetchedAt?: string;
}

/**
 * Mirror of the backend `CliCompletionContract` record (see
 * backend/Features/Cli/Execution/CliCompletionContracts.cs). Describes how one
 * CLI signals turn completion: which native frame each adapter maps to
 * `TurnCompleted` / `TurnFailed`, where the usage summary is read from, and
 * whether a typed `CliRunEvent` adapter exists at all (`typed`). Served by
 * `GET /api/cli/contracts` and rendered read-only on the Admin/CLI page.
 */
export interface CliCompletionContract {
  cliType: CliType;
  transport: string;
  sessionStartSignal: string;
  completionSignal: string;
  failureSignal: string;
  usageSource: string;
  typed: boolean;
  notes: string;
}

export interface CliSessionInfo {
  id: string;
  label: string | null;
  updatedAt: string | null;
  cwd: string | null;
  /**
   * On-disk transcript size in bytes. 0 when the CLI's inventory is index-only
   * (Codex), which the UI renders as "size unknown" rather than "0 B".
   */
  sizeBytes: number;
  lastUsage: SessionUsage | null;
  isProjectDefault: boolean;
  /**
   * Back-reference to the kanban task that owns this session, when the
   * session id appears in any job's `sessionChain`. Null for orphan
   * sessions (ad-hoc CLI use, sessions from another checkout). Drives
   * the small chip rendered next to the session row.
   */
  linkedJob: LinkedJobRef | null;
}

/**
 * Mirror of the backend `LinkedJobRef` record (see backend/Models/CliTypes.cs).
 * `lane` is the on-disk state slug; `isActive` is true when the owning job is
 * in `3-progress` AND the runner reports it as the project's currently-running
 * task. The frontend reads both to choose the chip colour rule.
 */
export interface LinkedJobRef {
  jobId: string;
  title: string;
  watchPath: string;
  projectName: string;
  lane: string;
  isActive: boolean;
}

export interface CliUsageProjectGroup {
  projectName: string;
  rootPath: string | null;
  sessions: CliSessionInfo[];
}

export interface CliUsageSection {
  cliType: CliType;
  available: boolean;
  version: string | null;
  path: string | null;
  error: string | null;
  projects: CliUsageProjectGroup[];
}

export interface CliUsageReport {
  at: string;
  sections: CliUsageSection[];
}
