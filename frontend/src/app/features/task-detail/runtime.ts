/**
 * Eager task-detail contracts used by the board and application shell.
 *
 * Keep this entrypoint free of components so importing selection and triage
 * state does not pull the task-detail view into the initial bundle.
 */
export { TaskSelectionService } from './state/task-selection.service';
export { TriageController } from './state/triage-controller.service';
export { LanePagerService } from './state/lane-pager.service';
export {
  AcceptanceRefusalStore,
  acceptanceRefusalFor,
  type AcceptanceRefusal,
} from './state/acceptance-refusal.model';
export {
  overflowActionsFor,
  primaryActionFor,
  laneLabelFor,
  mergeAcceptViewFor,
  acceptanceHoldReason,
  LANE_LABELS,
  triageDecisionRefusal,
  triageDecisionRowState,
  type MergeAcceptView,
  type TriageActionPayload,
  type TriageButton,
} from './state/triage-actions.model';
