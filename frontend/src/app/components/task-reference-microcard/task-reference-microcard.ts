import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import { projectIdentity } from '../../services/project-identity.util';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';
import { laneName, laneSentence, laneTone } from '../../models/lane-presentation';
import { TaskState } from '../../models/task.model';

export interface TaskReferenceMergeStatus {
  inIntegration: boolean;
  inRelease: boolean;
  integrationBranch: string;
  releaseBranch: string;
}

export interface TaskReferenceStatus {
  key: string;
  exists: boolean;
  taskKey: string | null;
  title: string | null;
  lane: string | null;
  projectId: string;
  projectName: string;
  projectColor: string | null;
  merge: TaskReferenceMergeStatus | null;
  reviewGrade: string | null;
}

@Component({
  selector: 'app-task-reference-microcard',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-reference-microcard.html',
  styleUrl: './task-reference-microcard.scss',
})
export class TaskReferenceMicrocardComponent {
  readonly status = input.required<TaskReferenceStatus>();
  /** Show key, title, and lane together when the reference is the primary receipt. */
  readonly expanded = input(false);
  readonly variant = input<'default' | 'lane-dot'>('default');
  readonly testId = input('task-reference-microcard');
  private readonly navigation = inject(TaskReferenceNavigationService);

  readonly color = computed(
    () => this.status().projectColor || projectIdentity(this.status().projectName).color,
  );
  readonly laneIcon = computed(() => laneChrome(this.status().lane).icon);
  readonly laneLabel = computed(() => laneChrome(this.status().lane).label);
  readonly laneStateTone = computed(() => laneChrome(this.status().lane).tone);
  readonly laneColorTone = computed(() => laneTone(this.status().lane));
  readonly mergeLabel = computed(() => {
    const merge = this.status().merge;
    if (!merge) return null;
    return `${merge.integrationBranch} ${merge.inIntegration ? 'merged' : 'not merged'}, ${merge.releaseBranch} ${merge.inRelease ? 'merged' : 'not merged'}`;
  });
  readonly mergePopoverLabel = computed(() => {
    const merge = this.status().merge;
    if (!merge) return null;
    const integrationStatus = merge.inIntegration ? 'merged' : 'open';
    const releaseStatus = merge.inRelease ? 'merged' : 'open';
    return `${merge.integrationBranch}: ${integrationStatus} · ${merge.releaseBranch}: ${releaseStatus}`;
  });
  readonly tooltipLabel = computed(() => [
    `Key: ${this.status().key}`,
    `Title: ${this.status().title || 'Unknown or deleted task'}`,
    `Lane: ${this.status().exists ? this.laneLabel() : 'Unavailable'}`,
    `State: ${this.status().exists ? laneSentence(this.status().lane) : 'Unknown or deleted task'}`,
    `Project: ${this.status().projectName}`,
    this.mergePopoverLabel(),
    this.status().reviewGrade ? `Review grade ${this.status().reviewGrade}` : null,
  ].filter(Boolean).join('\n'));

  open(event: MouseEvent): void {
    event.preventDefault();
    this.navigation.openTaskKey(this.status().taskKey);
  }
}

/**
 * Microcard lane chrome.
 *
 * AGT-2715: the label is the lane's one name from the presentation catalogue —
 * this used to collapse three lanes into the word "Waiting", so a card in Human
 * review read "Waiting" here and "Review" on the board. The coarse *tone*
 * bucket stays local for the default variant's glyph: that dense inline
 * control has five shape/colour states (`done`/`active`/`waiting`/`queued`/`ghost`).
 * The lane-dot variant instead binds the canonical per-lane tone.
 */
function laneChrome(lane: string | null): { icon: string; label: string; tone: string } {
  if (!lane) return { icon: '◇', label: 'Deleted or unknown task', tone: 'ghost' };
  const label = laneName(lane);
  if (lane === TaskState.Completed || lane === TaskState.Archive)
    return { icon: '✓', label, tone: 'done' };
  if (lane === TaskState.Progress || lane === TaskState.AutoReview)
    return { icon: '●', label, tone: 'active' };
  if (lane === TaskState.HumanReview || lane === TaskState.Escalated
      || lane === TaskState.CodeNotComplete)
    return { icon: '!', label, tone: 'waiting' };
  return { icon: '○', label, tone: 'queued' };
}
