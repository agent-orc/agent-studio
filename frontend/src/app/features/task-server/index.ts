/** Task-Server feature public API (AGT-1924). ADR-0034 barrel. */
export { TaskServerPanelComponent } from './components/task-server-panel/task-server-panel';
export { TaskServerClientsCardComponent } from './components/task-server-clients-card/task-server-clients-card';
export { TaskServerManagementPanelComponent } from './components/task-server-management-panel/task-server-management-panel';
export { ArchiveRetentionCardComponent } from './components/archive-retention-card/archive-retention-card';
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
  ArchiveTargetStatus,
  RetentionPlan,
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
