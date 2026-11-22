import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { forkJoin, map, of, Subscription, switchMap } from 'rxjs';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { serverMessage } from '../../services/project-deploy-data.service';
import { ManagedDatabaseBackupsComponent } from '../managed-database-backups/managed-database-backups';
import {
  connections, databaseState, formatBytes, readiness, volumeOverAlert,
  type DatabaseOperation, type DatabaseOperationRequest, type ManagedDatabase,
} from './managed-database.model';
import { PrincipalNamePipe } from '../../../../pipes/principal-name.pipe';

export { databaseState, formatBytes, type ManagedDatabase } from './managed-database.model';

interface EnvironmentList { environments: { declaration: { id: string; name: string } }[] }
interface EnvironmentDatabases { id: string; name: string; databases: ManagedDatabase[] }

/**
 * Managed databases per environment in the Bereitstellung area. Deleting data
 * is an owner action in two steps: the owner opens the confirmation, types the
 * database name, then confirms. Only retained databases offer it. Health and
 * the backups block (back up, test restore, restore, approvals) are per row.
 */
@Component({
  selector: 'app-managed-databases-panel',
  standalone: true,
  imports: [PrincipalNamePipe, DatePipe, StatusIndicatorComponent, ManagedDatabaseBackupsComponent],
  templateUrl: './managed-databases-panel.html',
  styleUrl: './managed-databases-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ManagedDatabasesPanelComponent {
  private readonly http = inject(HttpClient);
  private loadRequest?: Subscription;
  /** Advances on every project switch so responses started for another project are dropped. */
  private projectEpoch = 0;
  private displayedProject: string | null = null;
  readonly projectName = input.required<string>();
  readonly environments = signal<EnvironmentDatabases[]>([]);
  readonly withDatabases = computed(() => this.environments().filter(item => item.databases.length > 0));
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly confirming = signal<string | null>(null);
  readonly typed = signal('');
  readonly state = databaseState;
  readonly bytes = formatBytes;
  readonly readiness = readiness;
  readonly connections = connections;
  readonly overAlert = volumeOverAlert;

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      untracked(() => this.load(project));
      onCleanup(() => this.loadRequest?.unsubscribe());
    });
  }

  key(database: ManagedDatabase): string { return `${database.environmentId}/${database.name}`; }

  /** Other running databases of the same environment: the "new database" restore targets. */
  restoreTargets(environment: EnvironmentDatabases, database: ManagedDatabase): string[] {
    return environment.databases.filter(item => item.name !== database.name && item.state === 'running').map(item => item.name);
  }

  startDeletion(database: ManagedDatabase): void {
    this.confirming.set(this.key(database));
    this.typed.set('');
    this.notice.set(null);
  }

  cancelDeletion(): void { this.confirming.set(null); this.typed.set(''); }

  type(event: Event): void { this.typed.set((event.target as HTMLInputElement).value); }

  confirmDeletion(database: ManagedDatabase): void {
    if (this.typed() !== database.name) return;
    const project = this.projectName();
    const epoch = this.projectEpoch;
    this.busy.set(true);
    this.error.set(null);
    this.http.post(`${this.databaseUrl(project, database)}/deletion`, { confirm: this.typed() }).subscribe({
      next: () => {
        if (epoch !== this.projectEpoch) return;
        this.busy.set(false);
        this.cancelDeletion();
        this.notice.set(`Deletion of ${database.name} requested. The run host deletes the volume on its next poll.`);
        this.load(project);
      },
      error: error => {
        if (epoch !== this.projectEpoch) return;
        this.busy.set(false);
        this.error.set(serverMessage(error) ?? 'The deletion could not be requested.');
      },
    });
  }

  /** Sends one backup, test restore, restore, approve or reject request and reloads the panel when it is accepted. */
  runOperation(database: ManagedDatabase, request: DatabaseOperationRequest): void {
    const project = this.projectName();
    const epoch = this.projectEpoch;
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.http.post<{ operation?: DatabaseOperation } | null>(`${this.databaseUrl(project, database)}/${request.path}`, request.body)
      .subscribe({
        next: response => {
          if (epoch !== this.projectEpoch) return;
          this.busy.set(false);
          request.done?.();
          this.notice.set(request.notice(response?.operation ?? null));
          this.load(project);
        },
        error: error => {
          if (epoch !== this.projectEpoch) return;
          this.busy.set(false);
          this.error.set(serverMessage(error) ?? request.fallback);
        },
      });
  }

  private databaseUrl(project: string, database: ManagedDatabase): string {
    return `/api/projects/${encodeURIComponent(project)}/environments/${encodeURIComponent(database.environmentId)}`
      + `/databases/${encodeURIComponent(database.name)}`;
  }

  load(project: string): void {
    this.loadRequest?.unsubscribe();
    if (project !== this.displayedProject) {
      this.displayedProject = project;
      this.projectEpoch++;
      this.environments.set([]);
      this.busy.set(false);
      this.notice.set(null);
      this.cancelDeletion();
    }
    const epoch = this.projectEpoch;
    this.loading.set(true);
    this.error.set(null);
    const base = `/api/projects/${encodeURIComponent(project)}/environments`;
    this.loadRequest = this.http.get<EnvironmentList>(base).pipe(
      switchMap(list => list.environments.length
        ? forkJoin(list.environments.map(environment => this.http.get<{ databases: ManagedDatabase[] }>(
          `${base}/${encodeURIComponent(environment.declaration.id)}/databases`).pipe(
          map(data => ({ id: environment.declaration.id, name: environment.declaration.name, databases: data.databases })))))
        : of([] as EnvironmentDatabases[])),
    ).subscribe({
      next: items => {
        if (epoch !== this.projectEpoch) return;
        this.environments.set(items);
        this.loading.set(false);
      },
      error: () => {
        if (epoch !== this.projectEpoch) return;
        this.loading.set(false);
        this.error.set('Managed databases could not be loaded.');
      },
    });
  }
}
