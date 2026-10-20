import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { take } from 'rxjs';
import { metric, ratio, type OperationsLogResult, type ProjectOperations } from './project-operations.model';
import { OperationsTrendComponent } from '../operations-trend/operations-trend';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';

@Component({
  selector: 'app-project-operations', standalone: true, imports: [DatePipe, OperationsTrendComponent, LoadingSurfaceComponent],
  templateUrl: './project-operations.html', styleUrl: './project-operations.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectOperationsComponent {
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
  readonly telemetryAvailable = computed(() => this.snapshot()?.environments.some(row => row.telemetryAvailable) ?? false);
  readonly releaseOptions = computed(() => [...new Map((this.snapshot()?.environments ?? [])
    .flatMap(row => row.markers.map(marker => [marker.releaseId, marker.version] as const))).entries()]);
  readonly metric = metric;
  readonly ratio = ratio;

  constructor() { effect(() => this.load(this.projectName())); }

  load(project: string): void {
    const generation = ++this.readGeneration;
    ++this.logGeneration;
    this.snapshot.set(null); this.logs.set(null); this.error.set(null); this.release.set('');
    this.http.get<ProjectOperations>(`/api/projects/${encodeURIComponent(project)}/deploy/operations`).pipe(take(1)).subscribe({
      next: value => {
        if (generation !== this.readGeneration) return;
        this.snapshot.set(value); this.error.set(null);
        if (!value.environments.some(item => item.environment.id === this.selectedEnvironment()))
          this.selectedEnvironment.set(value.environments[0]?.environment.id ?? '');
      },
      error: () => { if (generation === this.readGeneration) this.error.set('Operations data could not be loaded.'); },
    });
  }

  search(): void {
    const environmentId = this.selectedEnvironment();
    if (!environmentId) return;
    const generation = ++this.logGeneration;
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
}
