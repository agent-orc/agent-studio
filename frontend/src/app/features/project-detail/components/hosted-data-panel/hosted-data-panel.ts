import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { forkJoin, map, of, Subscription, switchMap } from 'rxjs';

interface Volume { name: string; mountPath: string }
interface Backup { id: string; createdAt: string; bytes: number; sha256: string }
interface Schedule { volume: string; hourUtc: number | null; lastAttemptAt: string | null; lastError: string | null; backups: Backup[] }
interface Audit { id: string; at: string; actor: string; action: string; volume: string; backupId: string | null; outcome: string }
interface HostedData { database: { secretRef: string; environmentVariable: string } | null;
  volumes: Volume[]; schedules: Schedule[]; audit: Audit[]; adapterReady: boolean }
interface Environment { declaration: { id: string; name: string } }
interface EnvironmentList { environments: Environment[] }
interface EnvironmentData extends HostedData { id: string; name: string }

@Component({
  selector: 'app-hosted-data-panel',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './hosted-data-panel.html',
  styleUrl: './hosted-data-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostedDataPanelComponent {
  private readonly http = inject(HttpClient);
  private activeLoad?: Subscription;
  private loadGeneration = 0;
  private projectGeneration = 0;
  private displayedProject: string | null = null;
  readonly projectName = input.required<string>();
  readonly environments = signal<EnvironmentData[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly pendingRestore = signal<string | null>(null);
  readonly hours = Array.from({ length: 24 }, (_, hour) => hour);

  constructor() {
    effect(onCleanup => {
      this.load(this.projectName());
      onCleanup(() => {
        this.loadGeneration++;
        this.activeLoad?.unsubscribe();
      });
    });
  }

  schedule(environment: string, volume: string): Schedule | undefined {
    return this.environments().find(item => item.id === environment)?.schedules.find(item => item.volume === volume);
  }

  setSchedule(environment: string, volume: string, event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const hourUtc = value === '' ? null : Number(value);
    this.mutate(this.url(environment, volume) + '/schedule', 'put', { hourUtc }, 'Backup schedule saved.');
  }

  backup(environment: string, volume: string): void {
    this.mutate(this.url(environment, volume) + '/backups', 'post', {}, 'Backup completed.');
  }

  restore(environment: string, volume: string, backupId: string): void {
    const key = `${environment}/${volume}/${backupId}`;
    if (this.pendingRestore() !== key) { this.pendingRestore.set(key); return; }
    this.mutate(this.url(environment, volume) + `/backups/${encodeURIComponent(backupId)}/restore`,
      'post', {}, 'Volume restored from backup.');
    this.pendingRestore.set(null);
  }

  cancelRestore(): void { this.pendingRestore.set(null); }

  restoreLabel(environment: string, volume: string, backupId: string): string {
    return this.pendingRestore() === `${environment}/${volume}/${backupId}` ? 'Confirm restore' : 'Restore';
  }

  load(project: string): void {
    this.activeLoad?.unsubscribe();
    const generation = ++this.loadGeneration;
    if (project !== this.displayedProject) {
      this.displayedProject = project;
      this.projectGeneration++;
      this.busy.set(false);
      this.notice.set(null);
      this.pendingRestore.set(null);
      this.environments.set([]);
    }
    this.loading.set(true);
    this.error.set(null);
    this.activeLoad = this.http.get<EnvironmentList>(`/api/projects/${encodeURIComponent(project)}/environments`).pipe(
      switchMap(list => list.environments.length
        ? forkJoin(list.environments.map(environment => this.http.get<HostedData>(
          `/api/projects/${encodeURIComponent(project)}/environments/${encodeURIComponent(environment.declaration.id)}/volumes`).pipe(
          map(data => ({ ...data, id: environment.declaration.id, name: environment.declaration.name })))))
        : of([] as EnvironmentData[])),
    ).subscribe({
      next: items => {
        if (generation !== this.loadGeneration || project !== this.projectName()) return;
        this.environments.set(items);
        this.loading.set(false);
      },
      error: () => {
        if (generation !== this.loadGeneration || project !== this.projectName()) return;
        this.loading.set(false);
        this.error.set('Hosted data could not be loaded.');
      },
    });
  }

  private mutate(url: string, method: 'post' | 'put', body: object, message: string): void {
    const project = this.projectName();
    const generation = this.projectGeneration;
    this.busy.set(true);
    this.error.set(null);
    const request = method === 'post' ? this.http.post(url, body) : this.http.put(url, body);
    request.subscribe({
      next: () => {
        if (generation !== this.projectGeneration || project !== this.projectName()) return;
        this.busy.set(false);
        this.notice.set(message);
        this.load(project);
      },
      error: () => {
        if (generation !== this.projectGeneration || project !== this.projectName()) return;
        this.busy.set(false);
        this.error.set('The volume action failed. Check your access and the host backup adapter.');
      },
    });
  }

  private url(environment: string, volume: string): string {
    return `/api/projects/${encodeURIComponent(this.projectName())}/environments/${encodeURIComponent(environment)}`
      + `/volumes/${encodeURIComponent(volume)}`;
  }
}
