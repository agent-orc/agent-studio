import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import type { TaskDetail } from '../../../../models/task.model';

export type IncidentAction = 'Open Operations' | 'Roll back' | 'Run runbook';

/** Only the three routes written by HealthAlerts may become incident actions. */
export function incidentActionHref(markdown: string, action: IncidentAction): string | null {
  const escaped = action.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = markdown.match(new RegExp(`^- \\[${escaped}\\]\\((#[^\\s)]+)\\)`, 'm'));
  return match?.[1]?.startsWith('#/projects/') ? match[1] : null;
}

/** The header owns navigation; keep the evidence section focused on incident facts. */
export function incidentEvidenceMarkdown(markdown: string): string {
  const actions: IncidentAction[] = ['Open Operations', 'Roll back', 'Run runbook'];
  return markdown.split('\n').filter(line => !actions.some(action => {
    const href = incidentActionHref(line, action);
    return href !== null && line.trim() === `- [${action}](${href})`;
  })).join('\n');
}

@Component({
  selector: 'app-health-incident-card',
  standalone: true,
  imports: [MarkdownViewComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './health-incident-card.component.html',
  styleUrl: './health-incident-card.component.scss',
})
export class HealthIncidentCardComponent {
  readonly detail = input.required<TaskDetail>();
  readonly operations = computed(() => incidentActionHref(this.detail().promptMarkdown ?? '', 'Open Operations'));
  readonly rollback = computed(() => incidentActionHref(this.detail().promptMarkdown ?? '', 'Roll back'));
  readonly runbook = computed(() => incidentActionHref(this.detail().promptMarkdown ?? '', 'Run runbook'));
  readonly evidence = computed(() => incidentEvidenceMarkdown(this.detail().promptMarkdown ?? ''));
}
