import { historyRunStateTone } from '../../state/deploy-status-tone';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, type WritableSignal } from '@angular/core';
import { take } from 'rxjs';
import { metric, ratio, type OperationsLogResult, type ProjectOperations } from './project-operations.model';
import { OperationsTrendComponent } from '../operations-trend/operations-trend';
import { LoadingSurfaceComponent, PendingButtonDirective } from '../../../../components/async-feedback';
import { studioProjectSlug } from '../../../../services/studio-project-slug.util';

@Component({
  selector: 'app-project-operations', standalone: true, imports: [StatusIndicatorComponent, DatePipe, OperationsTrendComponent, LoadingSurfaceComponent, PendingButtonDirective],
  templateUrl: './project-operations.html', styleUrl: './project-operations.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectOperationsComponent {
  readonly markerTone = historyRunStateTone;
  private readonly http = inject(HttpClient);
  private readGeneration = 0;
  private logGeneration = 0;
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
  readonly loading = signal(false);
  readonly logLoading = signal(false);
  readonly telemetryAvailable = computed(() => this.snapshot()?.environments.some(row => row.telemetryAvailable) ?? false);
  readonly metric = metric;
  readonly ratio = ratio;
  readonly projectSlug = computed(() => studioProjectSlug(this.projectName()));

  constructor() { effect(() => this.load(this.projectName())); }

  load(project: string): void {
    const generation = ++this.readGeneration;
    ++this.logGeneration;
    this.snapshot.set(null); this.logs.set(null); this.error.set(null); this.release.set('');
    this.logLoading.set(false);
    this.loading.set(true);
    this.http.get<ProjectOperations>(`/api/projects/${encodeURIComponent(project)}/deploy/operations`).pipe(take(1)).subscribe({
      next: value => {
        if (generation !== this.readGeneration) return;
        this.snapshot.set(value); this.error.set(null); this.loading.set(false);
        if (!value.environments.some(item => item.environment.id === this.selectedEnvironment()))
          this.selectedEnvironment.set(value.environments[0]?.environment.id ?? '');
      },
      error: (failure: HttpErrorResponse) => { if (generation === this.readGeneration) {
        this.loading.set(false);
        this.error.set(failure.status === 503 ? 'Operations telemetry store read failed.' : 'Operations data could not be loaded.');
      } },
    });
  }

  changeFilter(filter: WritableSignal<string>, value: string): void {
    if (filter() === value) return;
    filter.set(value);
    ++this.logGeneration;
    this.logs.set(null);
    this.logError.set(null);
    this.logLoading.set(false);
  }

  search(append = false): void {
    const environmentId = this.selectedEnvironment();
    if (!environmentId) return;
    const previous = append ? this.logs() : null;
    if (append && !previous?.nextCursor) return;
    const generation = ++this.logGeneration;
    if (!append) this.logs.set(null);
    this.logError.set(null);
    this.logLoading.set(true);
    const params: Record<string, string> = { environmentId };
    if (append && previous?.nextCursor) params['cursor'] = previous.nextCursor;
    if (this.since()) params['since'] = new Date(this.since()).toISOString();
    if (this.until()) params['until'] = new Date(this.until()).toISOString();
    if (this.level()) params['level'] = this.level();
    if (this.release()) params['releaseId'] = this.release();
    if (this.searchText()) params['text'] = this.searchText();
    this.http.get<OperationsLogResult>(`/api/projects/${encodeURIComponent(this.projectName())}/deploy/operations/logs`,
      { params }).pipe(take(1)).subscribe({
      next: value => {
        if (generation === this.logGeneration) {
          this.logs.set(append && previous ? { ...value, lines: [...previous.lines, ...value.lines] } : value);
          this.logError.set(null); this.logLoading.set(false);
        }
      },
      error: (failure: HttpErrorResponse) => {
        if (generation === this.logGeneration) {
          this.logLoading.set(false);
          this.logError.set(failure.status === 503 ? 'Operations telemetry store read failed.' : 'Log search could not be completed.');
        }
      },
    });
  }
}
