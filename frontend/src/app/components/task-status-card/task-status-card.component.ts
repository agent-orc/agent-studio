import { ChangeDetectionStrategy, Component, ViewEncapsulation, computed, input } from '@angular/core';
import type { TaskInfo } from '../../models/task.model';
import { laneName, laneTone as laneToneFor } from '../../models/lane-presentation';
import { projectIdentity } from '../../services/project-identity.util';
import { cliTypeLabel } from '../../services/format.util';
import { InfoButtonComponent } from '../info-button/info-button.component';
import { laneDocTopic } from '../info-button/lane-doc-topic';

/**
 * Shared compact task status card. Used as:
 *   - `variant="popover"` inside a hover-popover (tab hover, board card hover)
 *   - `variant="inline"` embedded in a panel (activity tab, future Lane-Info modal)
 *
 * The card is intentionally read-only: lane, project + glyph, agent / CLI / model
 * meta, last-activity, and an optional run-status line. Actions live where the
 * card is hosted, not on the card itself.
 */
@Component({
  selector: 'app-task-status-card',
  standalone: true,
  imports: [InfoButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './task-status-card.component.html',
  styleUrl: './task-status-card.component.scss',
  host: {
    '[attr.data-variant]': 'variant()',
    'data-testid': 'task-status-card',
  },
})
export class TaskStatusCardComponent {
  readonly job = input.required<TaskInfo>();
  readonly variant = input<'popover' | 'inline'>('inline');

  readonly identity = computed(() => projectIdentity(this.job().projectName));

  readonly laneLabel = computed(() => laneName(this.job().state));

  /** The lane's one tone, shared with the board header and the Result view. */
  readonly laneTone = computed(() => laneToneFor(this.job().state));

  /** Concept-doc topic for the lane-info modal, or null when none exists. */
  readonly laneTopic = computed(() => laneDocTopic(this.job().state));
  readonly cliLabel = computed(() => {
    const t = this.job().cliType;
    return t ? cliTypeLabel(t) : null;
  });

  /** Effective model: shows the CLI-default hint when no per-task override is set. */
  readonly modelLabel = computed(() => this.job().model || 'CLI default');

  /** Owner / agent display. */
  readonly agentLabel = computed(() => this.job().agent || '—');

  /** Run status line — only present when a CLI execution is recorded. */
  readonly runStatus = computed(() => {
    const ex = this.job().execution;
    if (!ex) return null;
    const parts: string[] = [ex.status];
    if (ex.runOutcome) parts.push(ex.runOutcome);
    if (ex.durationSeconds !== null && ex.durationSeconds !== undefined) {
      parts.push(`${ex.durationSeconds}s`);
    }
    return parts.join(' · ');
  });

  readonly relativeLastActivity = computed(() => formatRelative(this.job().lastActivity));
  readonly absoluteLastActivity = computed(() => formatAbsolute(this.job().lastActivity));
}

function formatRelative(iso: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const diffMs = Date.now() - d.getTime();
  const minutes = Math.round(diffMs / 60_000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days}d ago`;
  const months = Math.round(days / 30);
  if (months < 12) return `${months}mo ago`;
  return `${Math.round(months / 12)}y ago`;
}

function formatAbsolute(iso: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString();
}
