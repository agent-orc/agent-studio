import { afterEveryRender, computed, effect, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import type { LaneCardGroup } from '../review-grouping.util';

/** Measured card window in the lane's existing scroll viewport. */
export class LaneCardWindow {
  private readonly viewport = signal({ top: 0, height: 900, visible: true });
  private readonly heights = new Map<string, number>();
  private readonly revision = signal(0);
  private readonly rowGap = signal(12);
  private readonly observer: ResizeObserver | null;
  private scrollElement: HTMLElement | null = null;
  private readonly onContainerScroll = () => {
    const body = this.body();
    if (body) this.updateViewport(body);
  };

  readonly enabled = computed(() => this.state() !== '7-archive' && this.state() !== '6-archive'
    && this.jobs().length > 40);
  private readonly orderedJobs = computed(() => this.isGrouped()
    ? this.groups().flatMap(group => group.jobs)
    : this.jobs());
  readonly window = computed(() => {
    const jobs = this.orderedJobs();
    this.revision();
    const viewport = this.viewport();
    const gap = this.rowGap();
    const estimate = this.compact() ? 100 : 240;
    const offsets = new Array<number>(jobs.length + 1);
    offsets[0] = 0;
    for (let i = 0; i < jobs.length; i++) {
      offsets[i + 1] = offsets[i] + (this.heights.get(jobs[i].taskKey) ?? estimate) + gap;
    }
    const lowerBound = (position: number) => {
      let lo = 0;
      let hi = jobs.length;
      while (lo < hi) {
        const mid = (lo + hi) >>> 1;
        if (offsets[mid] < position) lo = mid + 1;
        else hi = mid;
      }
      return lo;
    };
    const start = this.enabled() && viewport.visible ? Math.max(0, lowerBound(viewport.top) - 6) : 0;
    const end = this.enabled()
      ? (viewport.visible ? Math.min(jobs.length, lowerBound(viewport.top + viewport.height) + 7) : 0)
      : jobs.length;
    return {
      rows: jobs.slice(start, end).map((job, offset) => ({
        job,
        index: start + offset,
        group: this.isGrouped()
          ? this.groups().find(group => group.jobs[0]?.taskKey === job.taskKey) ?? null
          : null,
      })),
      start,
      end,
      top: offsets[start],
      bottom: offsets[jobs.length] - offsets[end],
      offsets,
    };
  });

  constructor(
    private readonly jobs: () => TaskInfo[],
    private readonly compact: () => boolean,
    private readonly state: () => string,
    private readonly isGrouped: () => boolean,
    private readonly groups: () => readonly LaneCardGroup[],
    private readonly body: () => HTMLElement | null,
    private readonly highlight: () => string | null,
  ) {
    this.observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(entries => {
      let changed = false;
      for (const entry of entries) {
        const element = entry.target as HTMLElement;
        const key = element.dataset['windowKey'];
        if (key) {
          const height = entry.borderBoxSize?.[0]?.blockSize ?? element.getBoundingClientRect().height;
          if (Math.abs((this.heights.get(key) ?? 0) - height) > 1) {
            this.heights.set(key, height);
            changed = true;
          }
        } else if (element === this.body() || element === this.scrollElement) {
          const body = this.body();
          if (body) this.updateViewport(body);
        }
      }
      if (changed) this.revision.update(value => value + 1);
    });
    afterEveryRender(() => {
      this.observer?.disconnect();
      const body = this.body();
      if (!body || !this.enabled()) {
        this.bindScrollElement(null);
        return;
      }
      const scroller = this.scrollerFor(body);
      this.bindScrollElement(scroller);
      this.observer?.observe(body);
      if (scroller !== body) this.observer?.observe(scroller);
      const rows = body.querySelectorAll<HTMLElement>('[data-window-key]');
      rows.forEach(row => this.observer?.observe(row));
      if (rows.length) {
        const gap = parseFloat(getComputedStyle(rows[0]).marginBottom);
        if (Number.isFinite(gap) && gap !== this.rowGap()) this.rowGap.set(gap);
      }
      this.updateViewport(body);
    });
    effect(() => {
      const highlighted = this.highlight();
      if (!highlighted || !this.enabled()) return;
      const index = this.orderedJobs().findIndex(job => job.id === highlighted);
      if (index < 0) return;
      requestAnimationFrame(() => {
        const body = this.body();
        if (!body) return;
        const { offsets } = this.window();
        const scroller = this.scrollerFor(body);
        const viewport = this.viewport();
        if (offsets[index] < viewport.top || offsets[index + 1] > viewport.top + viewport.height) {
          scroller.scrollTop += offsets[index] - viewport.top - viewport.height / 3;
          this.updateViewport(body);
        }
      });
    });
  }

  onScroll(event: Event): void {
    if (this.enabled()) this.updateViewport(event.currentTarget as HTMLElement);
  }

  onKeyDown(event: KeyboardEvent): void {
    if (!this.enabled() || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const target = event.target as HTMLElement;
    if (!target.matches('[data-testid="task-card"]')) return;
    const row = target.closest<HTMLElement>('[data-window-index]');
    const index = Number(row?.dataset['windowIndex']);
    if (!row || !Number.isInteger(index)) return;
    const body = this.body();
    if (!body) return;
    const count = this.orderedJobs().length;
    const scroller = this.scrollerFor(body);
    const page = Math.max(1, Math.floor(scroller.clientHeight / (this.compact() ? 100 : 240)));
    const step: Record<string, number> = {
      ArrowDown: index + 1, ArrowUp: index - 1,
      PageDown: index + page, PageUp: index - page,
      Home: 0, End: count - 1,
    };
    const requested = step[event.key];
    if (requested === undefined) return;
    event.preventDefault();
    const next = Math.max(0, Math.min(count - 1, requested));
    const offsets = this.window().offsets;
    scroller.scrollTop += offsets[next] - this.viewport().top - scroller.clientHeight / 3;
    this.updateViewport(body);
    requestAnimationFrame(() => {
      body.querySelector<HTMLElement>(`[data-window-index="${next}"] [data-testid="task-card"]`)?.focus();
    });
  }

  dropSlotAt(clientY: number, column: HTMLElement): number {
    if (!this.enabled()) {
      const cards = Array.from(column.querySelectorAll('app-job-card')) as HTMLElement[];
      for (let i = 0; i < cards.length; i++) {
        const rect = cards[i].getBoundingClientRect();
        if (clientY < rect.top + rect.height / 2) return i;
      }
      return cards.length;
    }
    const body = this.body();
    if (!body) return this.jobs().length;
    const position = (this.scrollerFor(body) === body ? body.scrollTop : 0)
      + clientY - body.getBoundingClientRect().top;
    const offsets = this.window().offsets;
    let lo = 0;
    let hi = this.jobs().length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if ((offsets[mid] + offsets[mid + 1]) / 2 < position) lo = mid + 1;
      else hi = mid;
    }
    return lo;
  }

  destroy(): void {
    this.observer?.disconnect();
    this.bindScrollElement(null);
  }

  private updateViewport(body: HTMLElement): void {
    const scroller = this.scrollerFor(body);
    const scrollerRect = scroller.getBoundingClientRect();
    const bodyRect = body.getBoundingClientRect();
    const top = scroller === body ? body.scrollTop : scrollerRect.top - bodyRect.top;
    const visible = scroller === body || (
      bodyRect.bottom >= scrollerRect.top && bodyRect.top <= scrollerRect.bottom
    );
    const next = { top: Math.max(0, top), height: scroller.clientHeight || 900, visible };
    const current = this.viewport();
    if (Math.abs(current.top - next.top) > 1 || current.height !== next.height || current.visible !== visible) this.viewport.set(next);
  }

  private scrollerFor(body: HTMLElement): HTMLElement {
    return getComputedStyle(body).overflowY === 'visible'
      ? body.closest<HTMLElement>('.lane-group__lanes') ?? body
      : body;
  }

  private bindScrollElement(next: HTMLElement | null): void {
    if (this.scrollElement === next) return;
    this.scrollElement?.removeEventListener('scroll', this.onContainerScroll);
    this.scrollElement = next;
    next?.addEventListener('scroll', this.onContainerScroll, { passive: true });
  }
}

export type LaneCardWindowView = ReturnType<LaneCardWindow['window']>;
