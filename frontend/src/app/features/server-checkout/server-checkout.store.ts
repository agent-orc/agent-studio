import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { AuthService } from '../../services/auth.service';
import { ProjectLookupService } from '../../services/project-lookup.service';
import { TaskService } from '../../services/task.service';
import type { RegistryProjectSummary } from '../../models/task.model';
// Service-only direct import avoids the shell <-> board component cycle.
import { WorkspaceManagerService } from '../shell/state/workspace-manager.service';
import { serverCheckoutView, type ServerCheckoutStatus, type ServerCheckoutView } from './server-checkout.model';

const POLL_MS = 1_000;

/**
 * AGT-3809 - the server checkout of local-profile URL projects. Settings,
 * the board, Ready cards, the create flow, and the first-run checklist read
 * one state here, so a checkout started on one surface shows its progress and
 * resulting path on every other one.
 */
@Injectable({ providedIn: 'root' })
export class ServerCheckoutStore {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly lookup = inject(ProjectLookupService);
  private readonly tasks = inject(TaskService);
  private readonly manager = inject(WorkspaceManagerService);
  private readonly statuses = signal<ReadonlyMap<string, ServerCheckoutStatus>>(new Map());
  private readonly polls = new Map<string, ReturnType<typeof setTimeout>>();

  /** Only the local profile creates checkouts from the UI; the networked one clones on registration. */
  readonly enabled = computed(() => this.auth.status()?.profile === 'local');

  /** Projects whose notice is visible: missing a checkout, or with a live or just finished job. */
  readonly noticeProjects = computed(() => this.enabled()
    ? this.lookup.allProjects().filter(project => this.viewFor(project) !== null) : []);

  projectFor(reference: { id?: string | null; name?: string | null }): RegistryProjectSummary | null {
    return this.lookup.allProjects().find(project =>
      (!!reference.id && project.id === reference.id)
      || (!!reference.name && project.displayName.toLowerCase() === reference.name.toLowerCase())) ?? null;
  }

  statusFor(projectId: string): ServerCheckoutStatus | null {
    return this.statuses().get(projectId) ?? null;
  }

  viewFor(project: RegistryProjectSummary | null): ServerCheckoutView | null {
    if (!project || !this.enabled()) return null;
    return serverCheckoutView(project, this.statusFor(project.id));
  }

  /** Reads the backend fact for one project; Settings calls it on open. */
  load(projectId: string): void {
    if (!this.enabled()) return;
    this.http.get<ServerCheckoutStatus>(this.url(projectId)).subscribe({
      next: status => this.apply(status),
      error: () => void 0,
    });
  }

  start(projectId: string): void {
    if (!this.enabled() || this.statusFor(projectId)?.state === 'running') return;
    const project = this.lookup.allProjects().find(item => item.id === projectId);
    this.apply({ projectId, state: 'running', step: 'clone', repositoryUrl: project ? project.repositoryUrl : null,
      repositoryPath: null, branch: null, error: null });
    this.http.post<ServerCheckoutStatus>(this.url(projectId), {}).subscribe({
      next: status => this.apply(status),
      error: (error: unknown) => this.apply({ projectId, state: 'failed', step: null,
        repositoryUrl: project?.repositoryUrl ?? null, repositoryPath: null, branch: null,
        error: startError(error) }),
    });
  }

  /** The create flow: the new project reaches the registry read first, so every notice can find it. */
  startForNewProject(projectId: string): void {
    this.refreshRegistry(() => this.start(projectId));
  }

  private apply(status: ServerCheckoutStatus): void {
    const previous = this.statusFor(status.projectId)?.state;
    this.statuses.update(map => new Map(map).set(status.projectId, status));
    const timer = this.polls.get(status.projectId);
    if (timer) clearTimeout(timer);
    this.polls.delete(status.projectId);
    if (status.state === 'running') {
      this.polls.set(status.projectId, setTimeout(() => this.load(status.projectId), POLL_MS));
    } else if (previous === 'running' && (status.state === 'ready' || status.state === 'present')) {
      this.refreshRegistry();
    }
  }

  /** The new repository path reaches every surface through the registry read. */
  private refreshRegistry(then?: () => void): void {
    this.tasks.getRegistryWorkspaces({ includeArchived: true }).subscribe({
      next: workspaces => {
        this.lookup.setWorkspaces(workspaces ?? []);
        this.manager.notifyRegistryChanged();
        then?.();
      },
      error: () => then?.(),
    });
  }

  private url(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}/server-checkout`;
  }
}

function startError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { error?: string } | null;
    if (body?.error) return body.error;
    if (error.status === 0) return 'Backend unreachable. Try again when it reconnects.';
    if (error.status === 403) return 'Only the Owner can create a server checkout.';
  }
  return 'The server checkout could not start. Try again.';
}
