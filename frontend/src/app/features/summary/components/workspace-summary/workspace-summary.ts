import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DatePipe } from '@angular/common';

import { TaskService } from '../../../../services/task.service';
import type { ExecutiveSummaryResponse } from '../../models/executive-summary.model';

const STORAGE_WINDOW_KEY = 'workspaceSummary.windowHours';

const WINDOW_OPTIONS: { hours: number; label: string; testId: string }[] = [
  { hours: 6, label: '6 h', testId: '6h' },
  { hours: 24, label: '24 h', testId: '24h' },
  { hours: 168, label: '7 days', testId: '7d' },
];

/**
 * Workspace-level executive summary: answers "what happened in the last
 * 6 / 24 / 168 hours?" by folding per-project activity (job moves,
 * decisions, advisories, commits), backend crash evidence, the
 * severity-ranked top decisions, and any open human-decision tasks into
 * one read-only board.
 *
 * The endpoint behind this is `GET /api/workspace/summary?windowHours=N`.
 * Window selection lives in localStorage so the user's last view is
 * preserved across sessions. Everything is a reference to a record on
 * disk; this surface never mutates state.
 */
@Component({
  selector: 'app-workspace-summary',
  standalone: true,
  imports: [SegmentedControlComponent, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-summary.html',
  styleUrl: './workspace-summary.scss',
})
export class WorkspaceSummaryComponent implements OnInit, OnDestroy {
  private readonly tasks = inject(TaskService);

  readonly windowOptions = WINDOW_OPTIONS;
  readonly segmentedWindowOptions = this.windowOptions.map(opt => ({ value: opt.hours, label: opt.label, testid: 'wsm-win-' + opt.testId }));

  readonly windowHours = signal<number>(this.loadWindow());
  readonly summary = signal<ExecutiveSummaryResponse | null>(null);
  readonly loading = signal<boolean>(false);
  readonly loaded = signal<boolean>(false);

  readonly byProject = computed(() => this.summary()?.byProject ?? []);
  readonly crashes = computed(() => this.summary()?.crashes ?? []);
  readonly topDecisions = computed(() => this.summary()?.topDecisions ?? []);
  readonly openHumanDecisions = computed(() => this.summary()?.openHumanDecisions ?? []);
  readonly headline = computed(() => this.summary()?.headline ?? '');

  readonly hasAnyActivity = computed(() => {
    const s = this.summary();
    if (!s) return false;
    return (
      s.byProject.length > 0 ||
      s.crashes.length > 0 ||
      s.topDecisions.length > 0 ||
      s.openHumanDecisions.length > 0
    );
  });

  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(true), 30_000);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  setWindow(hours: number): void {
    if (this.windowHours() === hours) return;
    this.windowHours.set(hours);
    try {
      localStorage.setItem(STORAGE_WINDOW_KEY, String(hours));
    } catch {
      /* ignore */
    }
    this.refresh();
  }

  refresh(silent = false): void {
    if (!silent) this.loading.set(true);
    this.tasks.getWorkspaceSummary(this.windowHours()).subscribe({
      next: (res) => {
        this.summary.set(res ?? null);
        this.loaded.set(true);
      },
      error: () => {
        /* keep prior summary */
      },
      complete: () => this.loading.set(false),
    });
  }

  severityClass(severity: string): string {
    switch (severity) {
      case 'Critical':
        return 'wsm__sev--critical';
      case 'High':
        return 'wsm__sev--high';
      case 'Warn':
        return 'wsm__sev--warn';
      default:
        return 'wsm__sev--info';
    }
  }

  private loadWindow(): number {
    try {
      const raw = localStorage.getItem(STORAGE_WINDOW_KEY);
      const n = raw ? Number(raw) : 24;
      return WINDOW_OPTIONS.some((o) => o.hours === n) ? n : 24;
    } catch {
      return 24;
    }
  }
}
