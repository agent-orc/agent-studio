import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
} from '@angular/core';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import type { TaskInfo } from '../../../../models/task.model';
import type { TaskCoreText, TaskCoreView } from '../../../../models/task-core.model';
import { laneLabelFor } from '../../state/triage-actions.model';
import { perfMark, perfMeasure } from '../../../../utils/perf-tracker';

interface LoadingSection {
  id: 'context' | 'activity' | 'evidence';
  label: string;
}

/** A bounded head as the template renders it. */
interface CoreHead {
  text: string | null;
  truncated: boolean;
}

const CORE_NOTICE: Partial<Record<TaskCoreView['state'], string>> = {
  warming: 'The task core is still warming on the server. Board facts are shown until it is ready.',
  missing: 'This task no longer exists.',
  denied: 'You no longer have access to this project.',
  error: 'The task core could not be read. The full detail is still loading.',
};

/**
 * Task route shell painted before the full detail arrives. Identity, lane,
 * pins and runtime come from the resident board record (the core seed) and
 * paint at once; the bounded prompt, status and timeline heads paint as soon
 * as the task core is available (AGT-2956). Git evidence is not part of the
 * core and keeps its own loading and error state, so a failed full-detail
 * request never blanks core content that is already visible.
 */
@Component({
  selector: 'app-task-detail-load-sections',
  standalone: true,
  imports: [DatePipe, NgTemplateOutlet],
  templateUrl: './task-detail-load-sections.component.html',
  styleUrl: './task-detail-load-sections.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskDetailLoadSectionsComponent {
  readonly info = input.required<TaskInfo>();
  /** Selected task core; ignored unless it belongs to `info`. */
  readonly core = input<TaskCoreView | null>(null);
  readonly errorMessage = input<string | null>(null);
  readonly back = output<void>();
  readonly retry = output<void>();

  readonly laneLabel = laneLabelFor;
  readonly sections: readonly LoadingSection[] = [
    { id: 'context', label: 'Task context' },
    { id: 'activity', label: 'Activity and result' },
    { id: 'evidence', label: 'Git evidence' },
  ];

  /** The core view for the task on screen; a view for another task is never shown. */
  readonly view = computed<TaskCoreView | null>(() => {
    const view = this.core();
    return view && view.seed.taskKey === this.info().taskKey ? view : null;
  });

  /** True once the bounded heads are on screen (`ready` or `stale`). */
  readonly headsReady = computed(() => !!this.view()?.core);

  readonly notice = computed(() => {
    const view = this.view();
    return view ? CORE_NOTICE[view.state] ?? null : null;
  });

  /** Sections the core answers; they stop waiting for the full detail. */
  readonly coreSettled = computed(() => {
    const state = this.view()?.state;
    return this.headsReady() || state === 'missing' || state === 'denied';
  });

  /** Section state label once the core answered for it. */
  readonly coreStateLabel = computed(() => {
    const state = this.view()?.state;
    if (state === 'missing' || state === 'denied') return 'Unavailable';
    return state === 'stale' ? 'Refreshing' : 'Summary';
  });

  readonly prompt = computed(() => this.head(this.view()?.core?.prompt));
  readonly status = computed(() => this.head(this.view()?.core?.statusSummary));
  readonly timeline = computed(() => {
    const timeline = this.view()?.core?.timeline;
    return {
      events: timeline?.events ?? [],
      truncated: !!timeline?.cursor,
    };
  });

  readonly runtime = computed(() => {
    const seed = this.view()?.seed;
    if (!seed) return null;
    const runtime = seed.runtime;
    const activity = runtime.activity?.kind;
    const state = activity && activity !== 'no-active-run' ? activity : runtime.executionStatus;
    return {
      state: state ? state.replace(/-/g, ' ') : 'Not running',
      location: runtime.location === 'none' ? null : runtime.location,
    };
  });

  private lastPaintedTask: string | null = null;

  constructor() {
    const injector = inject(Injector);
    // `task-core-select-to-painted`: selection until the bounded heads of
    // that task are in the DOM and a render has completed.
    effect(() => {
      const view = this.view();
      const taskKey = view?.core ? view.seed.taskKey : null;
      if (taskKey === this.lastPaintedTask) return;
      this.lastPaintedTask = taskKey;
      if (!taskKey) return;
      afterNextRender(() => {
        perfMark('task-core-painted');
        perfMeasure('task-core-select-to-painted', 'task-core-select', 'task-core-painted');
      }, { injector });
    });
  }

  private head(head: TaskCoreText | null | undefined): CoreHead {
    const text = head?.text?.trim() ? head.text : null;
    return { text, truncated: !!head?.cursor };
  }
}
