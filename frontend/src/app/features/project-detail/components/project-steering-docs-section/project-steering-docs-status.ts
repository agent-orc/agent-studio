import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { SteeringDocsWarningSeverity } from '../../../../models/steering-docs.model';

export interface SteeringAction {
  slug: string;
  label: string;
  description: string;
}

/** Steering-doc actions; each queues a normal 1-preparation task. */
export const STEERING_ACTIONS: SteeringAction[] = [
  { slug: 'summarize', label: 'Summarize Steering Docs', description: 'Spawn a task that reads the inventory below and produces a human summary of what agents are currently told.' },
  { slug: 'check-drift', label: 'Check Docs Drift', description: 'Spawn a task that compares the steering files against current code and flags stale rules or contradictions.' },
  { slug: 'analyze-failures', label: 'Analyze Recurring Job Failures', description: 'Spawn a task that scans recent blocked / needs-input outcomes and proposes steering-doc changes.' },
  { slug: 'propose-readme', label: 'Propose README Update', description: 'Spawn a task that drafts a README change for review, evidence-first.' },
  { slug: 'propose-agents', label: 'Propose AGENTS Update', description: 'Spawn a task that drafts an AGENTS.md change for review, evidence-first.' },
  { slug: 'create-followup', label: 'Create Follow-up Task', description: 'Queue a generic follow-up tied to the steering surface for later scoping.' },
];

const WARNING_SEVERITY_TONES: Record<SteeringDocsWarningSeverity, StatusTone> = {
  info: 'info',
  warn: 'warning',
  high: 'danger',
};

/** Tone of a steering-doc warning severity; an unknown severity is neutral. */
export function steeringWarningSeverityTone(severity: string): StatusTone {
  return WARNING_SEVERITY_TONES[severity as SteeringDocsWarningSeverity] ?? 'neutral';
}
