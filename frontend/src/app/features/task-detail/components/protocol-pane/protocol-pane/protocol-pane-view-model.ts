import type { TaskMode, TaskOutcomeIssue, TaskSummaryStatus } from '../../../../../models/task.model';
import type { ClaudeRateLimitSnapshot, ClaudeSessionInfo } from '../../../../../features/claude';
import type { PaneTabDef } from '../../../../../components/pane-tabs/pane-tabs.component';
import {
  formatTokens as fmtTokens,
  formatRateWindow as fmtRateWindow,
  formatResetIn as fmtResetIn,
  taskModeIcon,
  taskModeLabel,
} from '../../../../../services/format.util';

export interface ComposeModeBadge {
  mode: TaskMode;
  icon: string;
  label: string;
  /** True for the read-only modes ContinueModeGuardPolicy guards on the backend. */
  guarded: boolean;
}

/**
 * Derives the small mode badge shown next to the Activity composer's Send
 * button (AGT-2795), so an operator sees a concept or planning card is
 * read-only before the backend has to say so with a 409. Pure so the badge
 * shape is unit-testable without instantiating the component.
 */
export function deriveComposeMode(mode: TaskMode | undefined): ComposeModeBadge {
  const resolved = mode ?? 'coding';
  return {
    mode: resolved,
    icon: taskModeIcon(resolved),
    label: taskModeLabel(resolved),
    guarded: resolved === 'concept' || resolved === 'planning',
  };
}

/**
 * Builds the inspector tab strip (Task / Activity / Result) for the shared
 * pane-tabs component. Pure function so the protocol pane controller
 * stays compact and the tab catalogue is unit-testable in isolation.
 */
export function buildInspectorTabs(args: {
  summaryStatus: TaskSummaryStatus;
  hasStatusMarkdown: boolean;
  hasCliActivity: boolean;
  isHumanReview: boolean;
  isRunning: boolean;
}): readonly PaneTabDef[] {
  const protocolDisabled =
    !args.hasStatusMarkdown &&
    args.summaryStatus !== 'generating' &&
    args.summaryStatus !== 'failed' &&
    !args.hasCliActivity &&
    !args.isHumanReview;
  return [
    {
      id: 'task',
      label: 'Task',
      icon: 'file',
      testid: 'inspector-tab-task',
      modifier: 'task',
    },
    {
      id: 'activity',
      label: 'Activity',
      icon: 'activity',
      testid: 'inspector-tab-activity',
      indicator: args.isRunning ? 'live' : undefined,
      modifier: 'activity',
    },
    {
      // The user-facing area was renamed Protocol -> Result. The tab `id`
      // and `testid` stay `protocol` so the many inputs/specs keyed on them
      // keep working; only the visible label/emoji change.
      id: 'protocol',
      label: 'Result',
      icon: 'check',
      testid: 'inspector-tab-protocol',
      disabled: protocolDisabled,
      indicator: args.summaryStatus === 'generating' ? 'spinner' : undefined,
      modifier: 'protocol',
    },
  ];
}

export function formatTokens(n: number): string {
  return fmtTokens(n);
}

export function formatRateWindow(window: string | null): string {
  return fmtRateWindow(window);
}

export function formatResetIn(epoch: number, now: number): string {
  return fmtResetIn(epoch, now);
}

export function outcomeIssueExplanation(issue: TaskOutcomeIssue): string {
  switch (issue.kind) {
    case 'permission-blocked':
      return 'The orchestrator detected a permission failure. It gets one soft intervention that asks the agent to continue with the permissions already available. If the same category appears again, the task is routed to Review.';
    case 'watchdog-timeout':
      return 'The watchdog stopped a run after it stopped producing progress. The task is surfaced for human review with the concrete timeout category instead of a generic heuristic fallback.';
    case 'missing-terminal-sentinel':
      return 'The agent replied with useful completion text but did not emit a terminal sentinel. The orchestrator asks once for a proper sentinel and then accepts or stops with this visible category.';
    case 'classifier-unknown':
      return 'The runner could not map the reply to a known completion shape. This is tracked separately so repeated classifier misses are visible at task and project level.';
    case 'heuristic-done':
      return 'The runner accepted a completed-looking reply through the compatibility heuristic. The category remains visible so these cases can be reduced over time.';
    case 'task-branch-unpushed':
      return 'The runner finished the local task-branch handoff but could not push the task branch to origin after retry. The task can still be reviewed locally, but the branch is not durable for another machine until push succeeds.';
    default:
      return 'The runner attached a categorized outcome issue to this task. The raw source is still logs/cli-output.log.';
  }
}

export function formatIssueTime(iso: string | null | undefined): string {
  if (!iso) return 'unknown';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

export function claudeSessionTooltip(session: ClaudeSessionInfo | null): string {
  if (!session) return '';
  return [
    `Model: ${session.model ?? '?'}`,
    `Input: ${session.inputTokens.toLocaleString()} tokens`,
    `Output: ${session.outputTokens.toLocaleString()} tokens`,
    `Cache read: ${session.cacheReadTokens.toLocaleString()} tokens`,
    `Cache creation: ${session.cacheCreationTokens.toLocaleString()} tokens`,
    `Turns recorded: ${session.turnCount}`,
    session.lastTurnAt ? `Last turn: ${session.lastTurnAt}` : '',
  ]
    .filter(Boolean)
    .join('\n');
}

export function rateLimitTooltip(rateLimit: ClaudeRateLimitSnapshot | null, now: number): string {
  void now;
  if (!rateLimit) return '';
  const reset = rateLimit.resetsAt ? new Date(rateLimit.resetsAt * 1000).toLocaleString() : 'unknown';
  return [
    `Window: ${formatRateWindow(rateLimit.window)}`,
    `Status: ${rateLimit.status ?? '?'}`,
    `Resets at: ${reset}`,
    `Overage: ${rateLimit.overageStatus ?? '-'}`,
    rateLimit.isUsingOverage ? 'Currently using overage budget' : '',
    `Captured: ${new Date(rateLimit.capturedAt).toLocaleTimeString()}`,
  ]
    .filter(Boolean)
    .join('\n');
}
