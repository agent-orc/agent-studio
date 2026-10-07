import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { catchError, finalize, forkJoin, map, of, switchMap } from 'rxjs';
import { AuthSessionState } from '../../../../services/auth.service';
import type { RemoteHost } from '../../models/remote-host.model';
import type { StableReleaseIdentity } from '../../models/host-release-drift';
import {
  type FullBackupSummary, type InstallationIdentity, type ProjectRepositoryRead,
  type ProjectPlacement, type RegisteredProject, type RepositoryStatus, hostForProbe, latestRepositoryProbes,
  repositoryProofLabel,
} from '../../models/deployment-checkpoints';

/** Read-only installation checkpoints. Failed reads never imply a pass. */
@Component({
  selector: 'app-deployment-checkpoints',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './deployment-checkpoints.html',
  styleUrl: './deployment-checkpoints.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeploymentCheckpointsComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthSessionState);
  readonly hosts = input<readonly RemoteHost[]>([]);
  readonly stableRelease = input<StableReleaseIdentity | null>(null);
  readonly now = input(Date.now());
  readonly identity = signal<InstallationIdentity | null>(null);
  readonly identityError = signal<string | null>(null);
  readonly projects = signal<readonly ProjectRepositoryRead[]>([]);
  readonly projectsError = signal<string | null>(null);
  readonly backups = signal<readonly FullBackupSummary[]>([]);
  readonly backupsError = signal<string | null>(null);
  readonly loading = signal(false);
  readonly user = this.auth.status;
  readonly origin = typeof window === 'undefined' ? 'Unknown browser origin' : window.location.origin;
  readonly latestBackup = computed(() => [...this.backups()]
    .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))[0] ?? null);

  ngOnInit(): void { this.reload(); }

  reload(): void {
    this.loading.set(true);
    const identityRead = this.http.get<InstallationIdentity>('/api/v1/installation').pipe(
      map(identity => { this.identityError.set(null); return identity; }),
      catchError(() => {
        this.identityError.set('Installation identity could not be read. Check the Task Server route and retry.');
        return of(null);
      }),
    );
    const projectsRead = this.http.get<RegisteredProject[]>('/api/v1/projects').pipe(
      switchMap(projects => projects.length ? forkJoin(projects.map(project => {
        const prefix = `/api/v1/projects/${encodeURIComponent(project.projectId)}`;
        return forkJoin({
          repository: this.http.get<RepositoryStatus>(`${prefix}/repository`).pipe(
            map(status => ({ status, error: null })),
            catchError(error => of({ status: null,
              error: error?.status === 404
                ? 'No canonical repository registered. Register one through the owner project flow.'
                : 'Repository proof could not be read. Check the Task Server route and retry.' })),
          ),
          placement: this.http.get<ProjectPlacement>(`${prefix}/placement`).pipe(
            map(placement => ({ placement, placementError: null })),
            catchError(error => of({ placement: null,
              placementError: error?.status === 404
                ? 'No execution requirements recorded. Configure placement for this project.'
                : 'Execution requirements could not be read. Check the Task Server route and retry.' })),
          ),
        }).pipe(map(({ repository, placement }) => ({ project, ...repository, ...placement } as ProjectRepositoryRead)));
      })) : of([] as ProjectRepositoryRead[])),
      map(projects => { this.projectsError.set(null); return projects; }),
      catchError(() => {
        this.projectsError.set('Project registry could not be read. Check the Task Server route and retry.');
        return of([] as ProjectRepositoryRead[]);
      }),
    );
    const backupsRead = this.http.get<{ backups: FullBackupSummary[] }>('/api/v1/management/backups/full').pipe(
      map(result => { this.backupsError.set(null); return result.backups ?? []; }),
      catchError(() => {
        this.backupsError.set('Full backup inventory could not be read. Check management access and retry.');
        return of([] as FullBackupSummary[]);
      }),
    );
    forkJoin({ identity: identityRead, projects: projectsRead, backups: backupsRead })
      .pipe(finalize(() => this.loading.set(false))).subscribe({
      next: ({ identity, projects, backups }) => {
        this.identity.set(identity);
        this.projects.set(projects);
        this.backups.set(backups);
      },
    });
  }

  latestProbes(status: RepositoryStatus): RepositoryStatus['probes'] { return latestRepositoryProbes(status); }
  hostName(runnerId: string): string { return hostForProbe(this.hosts(), runnerId)?.name ?? runnerId; }
  proofLabel(probe: RepositoryStatus['probes'][number]): string {
    return repositoryProofLabel(probe, this.hosts(), this.now());
  }
  proofHostConnected(probe: RepositoryStatus['probes'][number]): boolean {
    return this.proofLabel(probe) === 'Last origin proof admitted; host connected. Re-probe to refresh.';
  }
}
