/**
 * Eager Task-Server contracts: the status service and model helpers.
 *
 * Keep this entrypoint free of components so a model import such as
 * `isLocalUrl` does not pull the Task-Server panels into the initial bundle.
 */
export { TaskServerService } from './services/task-server.service';
export type {
  TaskServerStatus,
  TaskServerConnection,
  TaskServerStore,
  TaskServerEvidenceStatus,
  TaskServerClient,
  TaskServerClientKind,
  TaskServerPhase,
  TaskServerHealth,
  ManagementActionKind,
  ManagementActionResult,
  StatusTone,
} from './models/task-server.model';
export {
  formatBytes,
  phaseLabel,
  healthLabel,
  healthTone,
  evidenceStateLabel,
  evidenceStateTone,
  clientKindLabel,
  managementActionLabel,
  formatRelativeTime,
  isLocalUrl,
} from './models/task-server.model';
