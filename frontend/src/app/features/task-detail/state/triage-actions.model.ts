/**
 * Lane-action catalogue for the detail view's primary-action + overflow
 * cluster (rendered in <app-detail-header>, top-right of the panel).
 *
 * The footer bar that previously hosted these buttons (the bottom-of-detail
 * "Human Review v" trigger + popover) was removed; the catalogue itself is
 * the same — index 0 is the lane's primary action and binds to Enter, the
 * remaining entries surface in the ⋯ overflow menu. Keeping this as a plain
 * TS module (no component) means the detail-header can render the cluster
 * directly without rehydrating a separate Angular component.
 */

import { TaskState } from '../../../models/task.model';
import type { TaskInfo } from '../../../models/task.model';
import { laneShortName } from '../../../models/lane-presentation';
import type { LandedState } from '../../../features/git';

export type TriageActionIntent =
  | { kind: 'move'; targetState: string }
  | { kind: 'moveToTop' }
  | { kind: 'delete' }
  | { kind: 'start' }
  | { kind: 'stop' }
  | { kind: 'editPrompt' }
  | { kind: 'showActivity' };

export interface TriageActionPayload {
  /** Stable id (e.g. 'mark-done') for testid + telemetry. */
  id: string;
  /** Display label, used by the parent for confirmation toasts. */
  label: string;
  intent: TriageActionIntent;
}

export type TriageButtonVariant = 'primary' | 'secondary' | 'danger';

export interface TriageButton {
  id: string;
  label: string;
  variant: TriageButtonVariant;
  intent: TriageActionIntent;
}

const PROMOTE_TO_PREP: TriageButton          = { id: 'promote-prep',       label: 'Promote to Preparation',           variant: 'primary',   intent: { kind: 'move', targetState: TaskState.Preparation } };
const PROMOTE_TO_READY: TriageButton         = { id: 'promote-ready',      label: 'Promote to Ready',                 variant: 'primary',   intent: { kind: 'move', targetState: TaskState.Ready } };
const PROMOTE_TO_READY_SKIP_PREP: TriageButton = { id: 'promote-ready-skip', label: 'Promote to Ready (skip prep)',  variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Ready } };
const FORCE_TO_READY: TriageButton           = { id: 'force-to-ready',     label: 'Force to Ready',                   variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Ready } };
const SEND_TO_BACKLOG: TriageButton          = { id: 'send-to-backlog',    label: 'Send to Backlog',                  variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Backlog } };
const SEND_TO_PREP: TriageButton             = { id: 'send-to-prep',       label: 'Send to Preparation',              variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Preparation } };
// "Move toward Delivered / Archive" mirror "Send to Backlog": plain move
// actions surfaced in the overflow menu from (almost) every lane so the
// operator can park a card in 6-completed / 7-archive without opening the
// lane dropdown. overflowActionsFor() handles dedup + current-lane skip.
export const MOVE_TO_COMPLETED: TriageButton = { id: 'move-to-completed',   label: 'Move to Delivered',                variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Completed } };
export const MOVE_TO_ARCHIVE: TriageButton   = { id: 'move-to-archive',     label: 'Move to Archive',                  variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Archive } };
export const EDIT_BUTTON: TriageButton       = { id: 'edit-prompt',        label: 'Edit prompt',                      variant: 'secondary', intent: { kind: 'editPrompt' } };
export const DELETE_BUTTON: TriageButton     = { id: 'delete',             label: 'Delete task',                      variant: 'danger',    intent: { kind: 'delete' } };

/**
 * Lane-specific button rows. Order matters: index 0 is the primary action
 * bound to Enter. Lanes that the user normally only observes
 * (`1a-orchestrator-prep`, `4-auto-review`) intentionally have no `primary`
 * entry — Enter is a no-op there to discourage rubber-stamp clicks.
 */
export const LANE_ACTIONS: Record<string, TriageButton[]> = {
  [TaskState.Backlog]: [
    PROMOTE_TO_PREP,
    PROMOTE_TO_READY_SKIP_PREP,
    EDIT_BUTTON,
    DELETE_BUTTON,
  ],
  [TaskState.Preparation]: [
    PROMOTE_TO_READY,
    SEND_TO_BACKLOG,
    EDIT_BUTTON,
  ],
  [TaskState.OrchestratorPrep]: [
    SEND_TO_BACKLOG,
    FORCE_TO_READY,
  ],
  [TaskState.Ready]: [
    { id: 'run-now',     label: 'Run now',      variant: 'primary',   intent: { kind: 'start' } },
    { id: 'move-to-top', label: 'Move to top',  variant: 'secondary', intent: { kind: 'moveToTop' } },
    SEND_TO_BACKLOG,
    EDIT_BUTTON,
  ],
  [TaskState.Progress]: [
    { id: 'stop-run',         label: 'Stop run',         variant: 'primary',   intent: { kind: 'stop' } },
    { id: 'view-live-output', label: 'View live output', variant: 'secondary', intent: { kind: 'showActivity' } },
  ],
  // 3b-code-not-complete: a task the runner parked after it exhausted its
  // auto-pickup retry budget without reaching review. The operator's natural
  // moves are to requeue it (after fixing context) or send it back for
  // preparation/triage.
  [TaskState.CodeNotComplete]: [
    { id: 'send-back-to-ready', label: 'Send back to Ready (re-do)', variant: 'primary',   intent: { kind: 'move', targetState: TaskState.Ready } },
    SEND_TO_PREP,
    SEND_TO_BACKLOG,
    EDIT_BUTTON,
  ],
  [TaskState.AutoReview]: [
    { id: 'force-accept', label: 'Force-accept (→ Review)', variant: 'secondary', intent: { kind: 'move', targetState: TaskState.HumanReview } },
    { id: 'reissue',      label: 'Reissue (→ Progress)',          variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Progress } },
  ],
  [TaskState.HumanReview]: [
    // "Merge into Develop" is the operator acceptance signal: it accepts the
    // task into 6-completed (the "Delivered" lane), which is the trigger the
    // deferred Merge-into-Develop post-step hooks into.
    { id: 'mark-done',          label: 'Merge into Develop',                        variant: 'primary',   intent: { kind: 'move', targetState: TaskState.Completed } },
    { id: 'send-back-to-ready', label: 'Send back to Ready (re-do)',                variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Ready } },
    SEND_TO_BACKLOG,
  ],
  [TaskState.Escalated]: [
    { id: 'reissue-escalated',        label: 'Continue (reissue)',      variant: 'primary',   intent: { kind: 'move', targetState: TaskState.Ready } },
    { id: 'accept-escalated',         label: 'Accept as-is',            variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Completed } },
    { id: 'manual-resolve-escalated', label: 'Resolve manually',        variant: 'secondary', intent: { kind: 'move', targetState: TaskState.HumanReview } },
    { id: 'discard-escalated',        label: 'Abort',                   variant: 'danger',    intent: { kind: 'move', targetState: TaskState.Archive } },
  ],
  [TaskState.Completed]: [
    // "Archive & Next" mirrors the review lanes' Complete-and-advance primary:
    // the move to 7-archive auto-advances the detail view to the next card in
    // 6-completed (TriageController.move → advanceToNextInLane). The "& Next"
    // wording makes the queue-sweep affordance explicit on the Completed lane.
    { id: 'archive', label: 'Archive & Next',      variant: 'primary',   intent: { kind: 'move', targetState: TaskState.Archive } },
    { id: 'reopen',  label: 'Re-open (→ Backlog)', variant: 'secondary', intent: { kind: 'move', targetState: TaskState.Backlog } },
  ],
  [TaskState.Archive]: [
    { id: 'restore', label: 'Restore (→ Backlog)', variant: 'primary', intent: { kind: 'move', targetState: TaskState.Backlog } },
  ],
};

/**
 * Re-export of the lane-pager projection so callers of this catalogue do not
 * need a second import. This used to be a byte-identical second copy of the
 * map, which is exactly how lane wording drifted (AGT-2715).
 */
export { LANE_LABELS } from './lane-pager.service';

/**
 * Lanes the orchestrator owns: a job lands here because the runner picked
 * it up (3-progress) or the auto-reviewer is judging it (4-auto-review),
 * never because an operator chose to park it there. They are therefore not
 * valid manual move targets — `overflowActionsFor` strips any move action
 * pointing at them so neither the context menu nor the (model-fed) studio
 * overflow offers them. The lane dropdowns drop them from their nav options
 * separately (they are navigation-only and these lanes are not pageable
 * targets either).
 */
export const ORCHESTRATOR_CONTROLLED_LANES: ReadonlySet<string> = new Set([
  TaskState.Progress,
  TaskState.AutoReview,
]);

/**
 * AGT-2069 — whether accepting `info` should surface the spawn-contract warning
 * (the AGT-1915 trap guard). True only when a planning task is being accepted
 * into `6-completed` and its spawn contract is not satisfied: it has spawned no
 * follow-up cards AND carries no deliberate "no follow-up intended" declaration.
 *
 * Coding / research tasks are never gated. A planning card whose backend
 * `planningSpawn` projection is absent (older payload) is not gated either — the
 * warning fires only on a *known*-unsatisfied contract, never on a guess.
 */
export function needsPlanningAcceptWarning(info: TaskInfo, targetState: string): boolean {
  if (targetState !== TaskState.Completed) return false;
  if (info.mode !== 'planning') return false;
  const spawn = info.planningSpawn;
  if (!spawn) return false;
  return !spawn.contractSatisfied;
}

/**
 * Temporary archive guard while the Status Dossier is still being decided.
 * Only the Delivered -> Archive transition is covered. An unknown integration
 * projection is treated as not integrated because archiving would otherwise
 * hide the unresolved delivery state without operator acknowledgement.
 */
export function needsUnintegratedArchiveWarning(info: TaskInfo, targetState: string): boolean {
  return info.state === TaskState.Completed
    && targetState === TaskState.Archive
    && info.integration?.status !== 'integrated';
}

export function laneActionsFor(state: string): TriageButton[] {
  return LANE_ACTIONS[state] ?? [];
}

export function laneLabelFor(state: string): string {
  return laneShortName(state);
}

/** Returns the lane's primary (index 0 with variant=primary) or null. */
export function primaryActionFor(state: string): TriageButton | null {
  const list = laneActionsFor(state);
  return list.length > 0 && list[0].variant === 'primary' ? list[0] : null;
}

/**
 * State-dependent presentation for the `5-human-review` acceptance primary
 * (`mark-done`, labelled "Merge into Develop"). The button literally offers a
 * merge, but a parallel-worktree run is auto-integrated into develop *before*
 * it lands in human-review (ADR-0052), so by the time the operator sees the
 * card the work has often already merged. Offering "Merge into Develop" then
 * lies. When the work has already landed, the header shows a read-only landed
 * status and relabels the button to a plain "Accept" - accepting the
 * already-merged work into Delivered, not triggering a merge. When nothing has
 * landed yet, the offer stays "Merge into Develop".
 */
export interface MergeAcceptView {
  /** True once the work is on develop (or further); drives status + relabel. */
  landed: boolean;
  /** Resolved lifecycle position used for the wording. */
  landedState: LandedState;
  /** Effective primary-button label. */
  acceptLabel: string;
  /** Status-pill text; null when not landed (no pill is shown). */
  statusLabel: string | null;
  /** Status-pill hover text; null when not landed. */
  statusTooltip: string | null;
}

function shortMergeSha(sha: string | null | undefined): string | null {
  const t = sha?.trim();
  if (!t) return null;
  return t.length > 7 ? t.slice(0, 7) : t;
}

/**
 * Resolve how the human-review acceptance primary should read for `info`. The
 * computed integration field is the only proof that attributed commits are
 * present on the target branch. The optional live landed-state hint may refine
 * an integrated result to released-to-main, but cannot prove integration.
 */
export function mergeAcceptViewFor(
  info: TaskInfo,
  landedStateHint: LandedState | null = null,
): MergeAcceptView {
  // Match the card merge-signal contract: only an attributed task commit is a
  // mergeable deliverable. A branch/base without one is a planning, docs,
  // results-only, or no-op outcome, even when its base is in the git graph.
  const hasTaskCommits = (info.commits?.length ?? 0) > 0 || !!info.commit;
  const mergeSha = shortMergeSha(info.integration?.sha);
  const landed = info.integration?.status === 'integrated';

  if (!hasTaskCommits) {
    return {
      landed: false,
      landedState: 'on-branch-only',
      acceptLabel: 'Accept',
      statusLabel: null,
      statusTooltip: 'This task has no code changes to merge. Accept moves the card to Delivered.',
    };
  }

  if (!landed) {
    return {
      landed: false,
      landedState: 'on-branch-only',
      acceptLabel: 'Merge into Develop',
      statusLabel: null,
      statusTooltip: null,
    };
  }

  let landedState: LandedState = landedStateHint ?? (landed ? 'merged-to-develop' : 'on-branch-only');
  if (landedState === 'on-branch-only' && landed) landedState = 'merged-to-develop';

  if (landedState === 'released-to-main') {
    return {
      landed: true,
      landedState,
      acceptLabel: 'Accept',
      statusLabel: 'Released to main',
      statusTooltip:
        "This task's work is already released to main. Accept moves the card to Delivered; no merge is triggered.",
    };
  }

  return {
    landed: true,
    landedState: 'merged-to-develop',
    acceptLabel: 'Accept',
    statusLabel: mergeSha ? `Merged to develop @${mergeSha}` : 'Merged to develop',
    statusTooltip: mergeSha
      ? `This task's work is already merged into develop at ${mergeSha}. Accept moves the card to Delivered; no merge is triggered.`
      : "This task's work is already merged into develop. Accept moves the card to Delivered; no merge is triggered.",
  };
}

/**
 * Returns the overflow actions for a lane — everything that is not the
 * primary action, plus Edit and Delete as guaranteed safety nets if the
 * lane catalogue did not already list them.
 */
export function overflowActionsFor(state: string): TriageButton[] {
  const list = laneActionsFor(state);
  const rest = (primaryActionFor(state) ? list.slice(1) : list.slice()).filter(
    b => b.intent.kind !== 'move' || !ORCHESTRATOR_CONTROLLED_LANES.has(b.intent.targetState),
  );

  // Edit / Delete form the trailing "safety net" cluster. Hold any that the
  // lane already lists aside so the Move entries land before them — otherwise
  // a lane like 2-ready (which lists Edit in its catalogue) would render
  // Edit *between* Send to Backlog and the Move entries.
  const isTail = (b: TriageButton) => b.id === EDIT_BUTTON.id || b.id === DELETE_BUTTON.id;
  const body = rest.filter(b => !isTail(b));
  const tail = rest.filter(isTail);

  // "Move to Delivered" / "Move to Archive" are guaranteed move actions,
  // added before the Edit/Delete cluster. Skip the target that equals the
  // current lane (a no-op) and any target a lane-specific action already
  // covers, so e.g. 5-human-review's "Merge into Develop" primary or
  // 6-completed's "Archive" primary is not duplicated.
  const laneTargets = new Set(
    list
      .map(b => (b.intent.kind === 'move' ? b.intent.targetState : null))
      .filter((s): s is string => s !== null),
  );
  if (state !== TaskState.Completed && !laneTargets.has(TaskState.Completed)) body.push(MOVE_TO_COMPLETED);
  if (state !== TaskState.Archive && !laneTargets.has(TaskState.Archive)) body.push(MOVE_TO_ARCHIVE);

  const items = [...body, ...tail];
  if (!items.some(i => i.id === EDIT_BUTTON.id)) items.push(EDIT_BUTTON);
  if (!items.some(i => i.id === DELETE_BUTTON.id)) items.push(DELETE_BUTTON);
  return items;
}
