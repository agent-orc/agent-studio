import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TaskExecutionLocation } from '../../models/task.model';

/**
 * The durable reason a runner refused an offered task.
 *
 * AGT-2818: it used to render the runner and the reason only. The reported card
 * (AGT-2738, refused on 2026-09-06 for a missing `task-server:connectivity`
 * capability) needed the other two facts to be actionable without opening
 * `task.json`: which refusal this is (`code`) and when it happened. The record
 * stays visible until a later dispatch succeeds or an operator clears it, which
 * is the store's contract, not this component's.
 */
@Component({
  selector: 'app-remote-dispatch-rejection',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './remote-dispatch-rejection.component.html',
  styleUrl: './remote-dispatch-rejection.component.scss',
})
export class RemoteDispatchRejectionComponent {
  readonly execution = input<TaskExecutionLocation | null | undefined>(null);
  readonly compact = input(false);
  readonly rejection = computed(() => this.execution()?.lastRejection ?? null);
  readonly runnerLabel = computed(() => {
    const rejection = this.rejection();
    return rejection?.runnerName || rejection?.runnerId || 'Remote Runner';
  });

  /** Clock plus date, because a refusal that is a week old reads as current. */
  readonly rejectedAtLabel = computed(() => {
    const value = this.rejection()?.rejectedAtUtc;
    if (!value) return null;
    const parsed = Date.parse(value);
    return Number.isNaN(parsed) ? value : new Date(parsed).toLocaleString();
  });
}
