import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { NowTickService } from '../../services/now-tick.service';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskPlanView, TaskPlanItemView, TaskPlanSubAction } from './plan.model';

/** A ticker mark with a flag for whether it sits past the soft-estimate band. */
interface TickerMark {
  past: boolean;
}

/**
 * Plan strip: a meta-level view above the activity log that surfaces the
 * CLI's own internal task plan (TodoWrite, update_plan, or TODO_LIST items). Each
 * item shows a title and a status; the active item gets a denominator-free
 * progress shape built from four telemetry-derived cues, and finished items
 * expand to reveal the sub-actions they consisted of.
 *
 * Pure presentation: every input is folded server-side by PlanReader from
 * append-only logs. No model call, no second LLM. If `plan` is null or has
 * no items the strip renders nothing, so it is safe to mount unconditionally
 * above the activity log.
 *
 * The four cues (see docs/mockups/task-progress-tracking):
 *  1. Live tool-call ticker - one mark per sub-action since the item activated.
 *  2. Latest sub-action label - the most recent tool's verb-plus-arg.
 *  3. Soft estimate band - a reference mark at the median sub-action count of
 *     already-done siblings; marks past it read as "taking longer than usual".
 *  4. Heartbeat pulse - pulses while work is recent, goes dim after 30 s idle.
 */
@Component({
  selector: 'app-plan-strip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './plan-strip.component.html',
  styleUrl: './plan-strip.component.scss',
})
export class PlanStripComponent {
  readonly plan = input.required<TaskPlanView | null>();
  readonly isRunning = input(false);
  readonly variant = input<'activity' | 'context'>('activity');

  private readonly nowTick = inject(NowTickService).now;
  private readonly destroyRef = inject(DestroyRef);

  /** Seconds of quiet after which the heartbeat stops pulsing. */
  private static readonly HeartbeatIdleSeconds = 30;

  private readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly changedItemIds = signal<ReadonlySet<string>>(new Set());
  private previousStatuses: ReadonlyMap<string, string> | null = null;
  private highlightTimer: ReturnType<typeof setTimeout> | null = null;

  readonly visible = computed<boolean>(() => {
    const p = this.plan();
    return !!p && p.hasPlan && p.items.length > 0;
  });

  readonly items = computed<TaskPlanItemView[]>(() => this.plan()?.items ?? []);
  readonly activeItemId = computed<string | null>(() => this.plan()?.activeItemId ?? null);
  readonly softEstimate = computed<number | null>(() => this.plan()?.softEstimateMedian ?? null);

  /** Overall completed / total - shown as muted context, not the progress claim. */
  readonly doneCount = computed<number>(() => this.items().filter((i) => i.status === 'done').length);
  readonly totalCount = computed<number>(() => this.items().length);

  /** "Before plan" bucket: tool calls that fired before the first plan landed. */
  readonly unassigned = computed<TaskPlanSubAction[]>(
    () => this.plan()?.unassignedSubActions ?? [],
  );

  readonly sourceLabel = computed<string>(() => {
    const s = this.plan()?.source ?? '';
    if (s.startsWith('claude')) return 'Claude';
    if (s.startsWith('codex')) return 'Codex';
    if (s.includes('heuristic')) return 'heuristic plan';
    return s || 'plan';
  });

  /** True for sources we cannot fully trust as native plan frames. */
  readonly isHeuristic = computed<boolean>(() => (this.plan()?.source ?? '').includes('heuristic'));

  readonly title = computed(() => this.variant() === 'context' ? 'Agent progress' : 'Agent plan');

  constructor() {
    effect(() => {
      const statuses = new Map((this.plan()?.items ?? []).map(item => [item.id, item.status]));
      untracked(() => {
        const previous = this.previousStatuses;
        this.previousStatuses = statuses;
        if (previous === null) return;
        const changed = new Set<string>();
        for (const [id, status] of statuses) {
          if (!previous.has(id) || previous.get(id) !== status) changed.add(id);
        }
        if (changed.size === 0) return;
        this.changedItemIds.set(changed);
        if (this.highlightTimer !== null) clearTimeout(this.highlightTimer);
        this.highlightTimer = setTimeout(() => {
          this.changedItemIds.set(new Set());
          this.highlightTimer = null;
        }, 1800);
      });
    });
    this.destroyRef.onDestroy(() => {
      if (this.highlightTimer !== null) clearTimeout(this.highlightTimer);
    });
  }

  isExpanded(id: string): boolean {
    return this.expanded().has(id);
  }

  isChanged(id: string): boolean {
    return this.changedItemIds().has(id);
  }

  toggle(id: string): void {
    const next = new Set(this.expanded());
    if (!next.delete(id)) next.add(id);
    this.expanded.set(next);
  }

  statusGlyph(status: string): string {
    switch (status) {
      case 'done':
        return '✓';
      case 'active':
        return '◆';
      default:
        return '○';
    }
  }

  /**
   * Ticker marks for an item: one per sub-action, each flagged with whether
   * it sits past the soft-estimate band so the template can colour the
   * "over budget" tail differently. Capped so a runaway item does not draw
   * hundreds of marks; the overflow count is shown as a numeric suffix.
   */
  ticker(item: TaskPlanItemView): TickerMark[] {
    const median = this.softEstimate();
    const cap = 40;
    const n = Math.min(item.subActionCount, cap);
    const marks: TickerMark[] = [];
    for (let i = 0; i < n; i++) {
      marks.push({ past: median != null && i >= median });
    }
    return marks;
  }

  tickerOverflow(item: TaskPlanItemView): number {
    return Math.max(0, item.subActionCount - 40);
  }

  /** The most recent sub-action label for the live "what is it doing now" line. */
  latestLabel(item: TaskPlanItemView): string | null {
    const subs = item.subActions;
    if (subs.length === 0) return null;
    const last = subs[subs.length - 1];
    return last.label ?? last.tool;
  }

  /**
   * Heartbeat state for the active item. `pulsing` while the run is live and
   * the last sub-action is recent; `dim` once the item has gone quiet past
   * the idle threshold (extended thinking, or stuck). Returns `off` for any
   * non-active item.
   */
  heartbeat(item: TaskPlanItemView): 'pulsing' | 'dim' | 'off' {
    if (item.status !== 'active') return 'off';
    if (!this.isRunning()) return 'dim';
    const subs = item.subActions;
    if (subs.length === 0) return 'pulsing';
    const lastTs = Date.parse(subs[subs.length - 1].ts);
    if (Number.isNaN(lastTs)) return 'pulsing';
    const idleSeconds = (this.nowTick() - lastTs) / 1000;
    return idleSeconds <= PlanStripComponent.HeartbeatIdleSeconds ? 'pulsing' : 'dim';
  }

  subActionTooltip(sub: TaskPlanSubAction): string {
    const when = new Date(sub.ts);
    const time = Number.isNaN(when.getTime()) ? sub.ts : when.toLocaleTimeString('en-US');
    return `${sub.label ?? sub.tool}\n${time}`;
  }
}
