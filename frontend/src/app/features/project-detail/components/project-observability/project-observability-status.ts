import { findingSeverityTone } from '../analysis-report-drilldown/analysis-report-status';

/** Outcome-issue topics shown as counter chips, with their label and chip tone. */
export const OUTCOME_ISSUE_TOPICS: readonly { topic: string; label: string; tone: 'warn' | 'high' }[] = [
  { topic: 'permission-blocked', label: 'Permission blocked', tone: 'high' },
  { topic: 'watchdog-timeout', label: 'Watchdog timeout', tone: 'high' },
  { topic: 'missing-terminal-sentinel', label: 'Missing sentinel', tone: 'warn' },
  { topic: 'classifier-unknown', label: 'Classifier unknown', tone: 'warn' },
  { topic: 'heuristic-done', label: 'Heuristic done', tone: 'warn' },
  { topic: 'soft-intervention', label: 'Soft intervention', tone: 'warn' },
];

/** Message severity to status indicator tone: Info neutral, Warn warning, High danger (AGT-3403). */
export const observabilitySeverityTone = findingSeverityTone;
