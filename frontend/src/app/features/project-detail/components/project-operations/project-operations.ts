import { historyRunStateTone } from '../../state/deploy-status-tone';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, HostListener, computed, effect, inject, input, output, signal, type WritableSignal } from '@angular/core';
import { take } from 'rxjs';
import { metric, ratio, type OperationsLogResult, type OperationsTrace, type OperationsSpan, type ProjectOperations } from './project-operations.model';
import { OperationsTrendComponent } from '../operations-trend/operations-trend';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { studioProjectSlug } from '../../../../services/studio-project-slug.util';

@Component({
  selector: 'app-project-operations', standalone: true, imports: [StatusIndicatorComponent, DatePipe, OperationsTrendComponent, LoadingSurfaceComponent],
  templateUrl: './project-operations.html', styleUrl: './project-operations.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectOperationsComponent {
  readonly markerTone = historyRunStateTone;
  private readonly http = inject(HttpClient);
  private readGeneration = 0;
  private logGeneration = 0;
  private traceGeneration = 0;
  readonly projectName = input.required<string>();
  readonly openRelease = output<string>();
  readonly openCard = output<string>();
  readonly snapshot = signal<ProjectOperations | null>(null);
  readonly error = signal<string | null>(null);
  readonly selectedEnvironment = signal('');
  readonly since = signal('');
  readonly until = signal('');
  readonly level = signal('');
  readonly release = signal('');
  readonly searchText = signal('');
  readonly logs = signal<OperationsLogResult | null>(null);
  readonly logError = signal<string | null>(null);
  readonly traces = signal<OperationsTrace[] | null>(null);
  readonly spans = signal<OperationsSpan[] | null>(null);
  readonly selectedTraceId = signal<string | null>(null);
  readonly traceError = signal<string | null>(null);
  readonly telemetryAvailable = computed(() => this.snapshot()?.environments.some(row => row.telemetryAvailable) ?? false);
  readonly metric = metric;
  readonly ratio = ratio;
  readonly projectSlug = computed(() => studioProjectSlug(this.projectName()));

  @HostListener('window:hashchange') onHashChange(): void { this.applyIncidentWindow(); }

  private applyIncidentWindow(): void {
    const query = window.location.hash.split('?')[1] ?? '';
    const params = new URLSearchParams(query);
    if (params.get('view') !== 'operations' || !params.get('environmentId') || !params.get('since')) return;
    const since = new Date(params.get('since')!);
    if (Number.isNaN(since.getTime())) return;
    this.selectedEnvironment.set(params.get('environmentId')!);
    this.since.set(new Date(since.getTime() - since.getTimezoneOffset() * 60_000).toISOString().slice(0, 16));
    this.until.set('');
    if (this.snapshot()) this.search();
  }

  constructor() { effect(() => this.load(this.projectName())); }

  load(project: string): void {
    const generation = ++this.readGeneration;
    ++this.logGeneration;
    ++this.traceGeneration;
    this.snapshot.set(null); this.logs.set(null); this.error.set(null); this.release.set('');
    this.traces.set(null); this.spans.set(null); this.selectedTraceId.set(null); this.traceError.set(null);
    this.http.get<ProjectOperations>(`/api/projects/${encodeURIComponent(project)}/deploy/operations`).pipe(take(1)).subscribe({
      next: value => {
        if (generation !== this.readGeneration) return;
        this.snapshot.set(value); this.error.set(null);
        if (!value.environments.some(item => item.environment.id === this.selectedEnvironment()))
          this.selectedEnvironment.set(value.environments[0]?.environment.id ?? '');
        this.applyIncidentWindow();
      },
      error: () => { if (generation === this.readGeneration) this.error.set('Operations data could not be loaded.'); },
    });
  }

  changeFilter(filter: WritableSignal<string>, value: string): void {
    if (filter() === value) return;
    filter.set(value);
    ++this.logGeneration;
    ++this.traceGeneration;
    this.logs.set(null);
    this.logError.set(null);
    this.traces.set(null); this.spans.set(null); this.selectedTraceId.set(null);
  }

  search(): void {
    const environmentId = this.selectedEnvironment();
    if (!environmentId) return;
    const generation = ++this.logGeneration;
    this.logs.set(null);
    this.logError.set(null);
    const params: Record<string, string> = { environmentId };
    if (this.since()) params['since'] = new Date(this.since()).toISOString();
    if (this.until()) params['until'] = new Date(this.until()).toISOString();
    if (this.level()) params['level'] = this.level();
    if (this.release()) params['releaseId'] = this.release();
    if (this.searchText()) params['text'] = this.searchText();
    this.http.get<OperationsLogResult>(`/api/projects/${encodeURIComponent(this.projectName())}/deploy/operations/logs`,
      { params }).pipe(take(1)).subscribe({
      next: value => {
        if (generation === this.logGeneration) { this.logs.set(value); this.logError.set(null); }
      },
      error: () => {
        if (generation === this.logGeneration) { this.logs.set(null); this.logError.set('Log search could not be completed.'); }
      },
    });
  }

  searchTraces(): void {
    const environment = this.selectedEnvironment();
    if (!environment) return;
    const generation = ++this.traceGeneration;
    const params: Record<string, string> = {};
    if (this.since()) params['from'] = new Date(this.since()).toISOString();
    if (this.until()) params['to'] = new Date(this.until()).toISOString();
    this.traces.set(null); this.traceError.set(null); this.spans.set(null); this.selectedTraceId.set(null);
    this.http.get<OperationsTrace[]>(this.traceBase(environment) + '/traces', { params }).pipe(take(1)).subscribe({
      next: traces => { if (generation === this.traceGeneration) this.traces.set(traces); },
      error: () => { if (generation === this.traceGeneration) this.traceError.set('Trace search could not be completed.'); },
    });
  }

  openTrace(traceId: string): void {
    const environment = this.selectedEnvironment();
    if (!environment) return;
    const generation = ++this.traceGeneration;
    this.selectedTraceId.set(traceId); this.spans.set(null); this.traceError.set(null);
    this.http.get<OperationsSpan[]>(this.traceBase(environment) + '/traces/' + encodeURIComponent(traceId))
      .pipe(take(1)).subscribe({
        next: spans => { if (generation === this.traceGeneration) this.spans.set(spans); },
        error: () => { if (generation === this.traceGeneration) this.traceError.set('Trace could not be loaded.'); },
      });
  }

  spanOffset(span: OperationsSpan): number {
    const rows = this.spans() ?? [];
    const first = Math.min(...rows.map(row => Date.parse(row.startedAt)));
    const total = Math.max(...rows.map(row => Date.parse(row.startedAt) + row.durationMs)) - first;
    return total > 0 ? Math.max(0, (Date.parse(span.startedAt) - first) / total * 100) : 0;
  }

  spanWidth(span: OperationsSpan): number {
    const rows = this.spans() ?? [];
    const first = Math.min(...rows.map(row => Date.parse(row.startedAt)));
    const total = Math.max(...rows.map(row => Date.parse(row.startedAt) + row.durationMs)) - first;
    return total > 0 ? Math.max(0.5, span.durationMs / total * 100) : 100;
  }

  private traceBase(environment: string): string {
    return `/api/projects/${encodeURIComponent(this.projectName())}/environments/${encodeURIComponent(environment)}/telemetry`;
  }
}
