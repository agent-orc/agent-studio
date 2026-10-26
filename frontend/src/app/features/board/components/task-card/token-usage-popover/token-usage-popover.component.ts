import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskInfo } from '../../../../../models/task.model';
import { TaskService } from '../../../../../services/task.service';
import { tokenAnalysisCardHash } from '../../../../token-analysis/token-analysis-navigation.util';
import type { TaskTokenBubble } from '../task-card-view-model';
import { formatTokens } from '../task-card-view-model';
import { buildTypeBreakdown, type TypeBreakdownRow } from './token-usage-popover.util';

/**
 * The board card's token-usage popover. Split out of `TaskCardComponent`
 * (folder-per-component + component size budget) because it owns its own
 * lazy fetch: the by-type breakdown needs the job's pipeline execution
 * record (`GET /tasks/{id}/pipeline`), which is too expensive to preload
 * for every card on the board, so it is fetched once on first open.
 *
 * `TokenPopoverDirective` (applied on the card's wrapper) still owns
 * show/hide/portal placement; it locates this component's host via
 * `[data-token-popover]` exactly as it did the old inline `<span>`. This
 * component is a *sibling* of the trigger button, not an ancestor/descendant,
 * so hovering the trigger never bubbles into it — the wrapper (which sees
 * both the trigger and this component) calls {@link ensureTypeBreakdownLoaded}
 * directly via a template reference; see `task-card.component.html`.
 */
@Component({
  selector: 'app-task-token-usage-popover',
  exportAs: 'tokenUsagePopover',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-usage-popover.component.html',
  styleUrl: './token-usage-popover.component.scss',
})
export class TaskTokenUsagePopoverComponent {
  readonly job = input.required<TaskInfo>();
  readonly bubble = input<TaskTokenBubble | null>(null);

  private readonly taskService = inject(TaskService);

  readonly typeBreakdownState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly typeBreakdown = signal<TypeBreakdownRow[]>([]);

  formatTokens(n: number): string {
    return formatTokens(n);
  }

  /** Link to the card's token drill-down in the workspace token analysis (AGT-3267). */
  analysisHref(): string {
    const job = this.job();
    return tokenAnalysisCardHash('', job.projectName, job.id);
  }

  ensureTypeBreakdownLoaded(): void {
    if (this.typeBreakdownState() !== 'idle') return;
    this.typeBreakdownState.set('loading');
    const job = this.job();
    this.taskService.getJobPipeline(job.id, job.watchPath ?? undefined).subscribe({
      next: (response) => {
        if (response?.cost) this.typeBreakdown.set(buildTypeBreakdown(response.cost));
        this.typeBreakdownState.set('loaded');
      },
      error: () => this.typeBreakdownState.set('error'),
    });
  }
}
