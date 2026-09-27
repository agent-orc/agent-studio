import type { ClaudeRateLimitSnapshot, ClaudeSessionInfo } from '../../claude';
import type { CliType, TaskInfo } from '../../../models/task.model';
import {
  cliTypeLabel as fmtCliTypeLabel,
  formatDate as fmtDate,
  formatDateTime as fmtDateTime,
  formatMultiplier as fmtMultiplier,
  formatRateWindow as fmtRateWindow,
  formatResetIn as fmtResetIn,
  formatTime as fmtTime,
  formatTokens as fmtTokens,
  stateLabel as fmtStateLabel,
} from '../../../services/format.util';

export function formatTokens(n: number): string {
  return fmtTokens(n);
}

export function formatMultiplier(mult: number | null): string {
  return fmtMultiplier(mult);
}

export function cliTypeLabel(cliType: CliType): string {
  return fmtCliTypeLabel(cliType);
}

export function claudeSessionTooltip(session: ClaudeSessionInfo | null): string {
  if (!session) return '';
  return [
    `Model: ${session.model ?? '?'}`,
    `Input: ${session.inputTokens.toLocaleString('en-US')} tokens`,
    `Output: ${session.outputTokens.toLocaleString('en-US')} tokens`,
    `Cache read: ${session.cacheReadTokens.toLocaleString('en-US')} tokens`,
    `Cache creation: ${session.cacheCreationTokens.toLocaleString('en-US')} tokens`,
    `Turns recorded: ${session.turnCount}`,
    session.lastTurnAt ? `Last turn: ${session.lastTurnAt}` : '',
  ]
    .filter(Boolean)
    .join('\n');
}

export function formatRateWindow(window: string | null): string {
  return fmtRateWindow(window);
}

export function formatResetIn(epochSeconds: number, now: number): string {
  return fmtResetIn(epochSeconds, now);
}

export function rateLimitTooltip(rateLimit: ClaudeRateLimitSnapshot | null): string {
  if (!rateLimit) return '';
  const reset = rateLimit.resetsAt ? new Date(rateLimit.resetsAt * 1000).toLocaleString('en-US') : 'unknown';
  return [
    `Window: ${formatRateWindow(rateLimit.window)}`,
    `Status: ${rateLimit.status ?? '?'}`,
    `Resets at: ${reset}`,
    `Overage: ${rateLimit.overageStatus ?? '-'}`,
    rateLimit.isUsingOverage ? 'Currently using overage budget' : '',
    `Captured: ${new Date(rateLimit.capturedAt).toLocaleTimeString('en-US')}`,
  ]
    .filter(Boolean)
    .join('\n');
}

export function stateLabel(state: string): string {
  return fmtStateLabel(state);
}

export function formatTime(dateStr: string): string {
  return fmtTime(dateStr);
}

export function formatDate(dateStr: string): string {
  return fmtDate(dateStr);
}

export function formatDateTime(dateStr: string): string {
  return fmtDateTime(dateStr);
}

export function isCliErrorMessage(message: string | null | undefined): boolean {
  return !!message && /cli|authenticat/i.test(message);
}

/**
 * Commit count + tooltip for the Git pane-toggle badge. Replaces the
 * legacy `COMMITTED N commits` inline strip above the activity log; the
 * Git pane already shows the full commit list with files and diffs.
 * Reads `TaskInfo.commits` (preferred) and falls back to the legacy
 * singular `TaskInfo.commit` for unmigrated jobs. Tooltip is null when
 * there are no commits — callers fall back to the default Git tooltip.
 */
export function gitCommitCount(info: TaskInfo): number {
  return info.commits?.length || (info.commit ? 1 : 0);
}

export function gitToggleTooltip(info: TaskInfo): string | null {
  const commits = info.commits?.length ? info.commits : (info.commit ? [info.commit] : []);
  const n = commits.length;
  if (n === 0) return null;
  const files = commits.reduce((s, c) => s + (c.filesChanged || 0), 0);
  const tail = files > 0
    ? `${n} commit${n === 1 ? '' : 's'} · ${files} file${files === 1 ? '' : 's'}`
    : `${n} commit${n === 1 ? '' : 's'}`;
  return `Git diff & file tree · ${tail}`;
}
