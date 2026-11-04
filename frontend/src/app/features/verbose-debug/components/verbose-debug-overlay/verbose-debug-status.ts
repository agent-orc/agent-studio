import type { ToolFamily } from 'coding-agent-chat/core';
import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Warning-density row threshold tone. */
export type WarningRowTone = 'info' | 'warn' | 'danger';

const DECISION_SEVERITY_TONES: Readonly<Record<string, StatusTone>> = { info: 'info', warn: 'warning', error: 'danger' };
const WARNING_ROW_TONES: Readonly<Record<WarningRowTone, StatusTone>> = { info: 'neutral', warn: 'warning', danger: 'danger' };
const RUN_STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  running: 'running', completed: 'success', failed: 'danger', cancelled: 'neutral',
};
const ARTIFACT_STATUS_TONES: Readonly<Record<string, StatusTone>> = { passed: 'success', failed: 'danger' };

/** Orchestrator decision severity; a missing severity reads as info. */
export function decisionSeverityTone(severity: string | null | undefined): StatusTone {
  return DECISION_SEVERITY_TONES[severity ?? 'info'] ?? 'neutral';
}

/** Warning-density threshold tone; under the warn threshold is nothing to report. */
export function warningRowTone(tone: string): StatusTone {
  return WARNING_ROW_TONES[tone as WarningRowTone] ?? 'neutral';
}

export function debugRunStatusTone(status: string | null | undefined): StatusTone {
  return RUN_STATUS_TONES[(status ?? '').toLowerCase()] ?? 'neutral';
}

export function artifactStatusTone(status: string | null | undefined): StatusTone {
  return ARTIFACT_STATUS_TONES[(status ?? '').toLowerCase()] ?? 'neutral';
}

export const TOOL_LABELS: Record<ToolFamily, string> = {
  read: 'Read',
  search: 'Search',
  command: 'Command',
  edit: 'Edit',
  task: 'Task',
  todo: 'Todo',
  other: 'Other',
};
