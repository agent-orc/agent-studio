import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import {
  GitStatePerformance,
  GitStatePerformanceService,
} from '../../../../services/git-state-performance.service';
import {
  clearVisibleInterval,
  setVisibleInterval,
  type VisibleIntervalHandle,
} from '../../../../utils/visible-interval';

const REFRESH_MS = 15_000;

/**
 * Admin performance panel for the board's git-derived state (AGT-2726).
 *
 * Answers the three questions an operator has when the board feels slow: how
 * long each endpoint takes (p50 and p95 over the last hour), how many git
 * processes the backend is starting per minute, and how old each repository's
 * indexed state is. The warnings come from the backend's own service-level
 * rules, so the panel never re-derives a threshold.
 */
@Component({
  selector: 'app-workspace-performance-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DecimalPipe],
  templateUrl: './workspace-performance-panel.component.html',
  styleUrl: './workspace-performance-panel.component.scss',
})
export class WorkspacePerformancePanelComponent implements OnInit, OnDestroy {
  private readonly performance = inject(GitStatePerformanceService);
  private handle: VisibleIntervalHandle | null = null;

  readonly report = signal<GitStatePerformance | null>(null);
  readonly error = signal<string | null>(null);
  readonly loading = signal(true);

  readonly endpoints = computed(() => this.report()?.endpoints ?? []);
  readonly repositories = computed(() => this.report()?.repositories ?? []);
  readonly warnings = computed(() => this.report()?.warnings ?? []);
  readonly spawnsPerMinute = computed(() => this.report()?.spawnsPerMinute ?? 0);

  ngOnInit(): void {
    this.refresh();
    this.handle = setVisibleInterval(() => this.refresh(), REFRESH_MS);
  }

  ngOnDestroy(): void {
    clearVisibleInterval(this.handle);
    this.handle = null;
  }

  refresh(): void {
    this.performance.load().subscribe({
      next: report => {
        this.report.set(report);
        this.error.set(null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('The performance rollup could not be read.');
        this.loading.set(false);
      },
    });
  }

  /** Rounds to whole milliseconds; sub-millisecond readings render as 0. */
  ms(value: number): string {
    return `${Math.round(value)} ms`;
  }

  age(repository: { ageSeconds: number | null }): string {
    if (repository.ageSeconds === null) return 'not indexed';
    if (repository.ageSeconds < 60) return `${Math.round(repository.ageSeconds)} s ago`;
    return `${Math.round(repository.ageSeconds / 60)} min ago`;
  }

  endpointIdentity(_index: number, endpoint: { label: string }): string {
    return endpoint.label;
  }

  repositoryIdentity(_index: number, repository: { repositoryRoot: string }): string {
    return repository.repositoryRoot;
  }
}
