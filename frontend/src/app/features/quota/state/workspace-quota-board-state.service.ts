import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

/** Admission view of one quota scope, as `/api/workspaces/{id}[/projects/{id}]/quota` returns it. */
export interface WorkspaceQuotaAdmission {
  status: string;
  message: string | null;
  overrideActive: boolean;
  codingAllowed: boolean;
}

/** One project of the workspace whose quota the board reads. */
export interface WorkspaceQuotaProjectRef {
  id: string;
  displayName: string;
}

@Injectable({ providedIn: 'root' })
export class WorkspaceQuotaBoardState {
  private readonly http = inject(HttpClient);
  private readonly workspaceMessage = signal<string | null>(null);
  private readonly projectMessages = signal<Record<string, string>>({});
  /** Workspace quota for the Studio header; null before the first read and after a failed one. */
  readonly workspaceQuota = signal<WorkspaceQuotaAdmission | null>(null);
  private loadedWorkspace: string | null = null;
  private sequence = 0;

  reset(): void {
    this.sequence++;
    this.loadedWorkspace = null;
    this.workspaceQuota.set(null);
    this.workspaceMessage.set(null);
    this.projectMessages.set({});
  }

  /**
   * Reads the workspace and per-project admission. Switching workspace drops
   * the previous messages at once; a superseded read never lands.
   */
  load(workspaceId: string, projects: readonly WorkspaceQuotaProjectRef[]): void {
    if (workspaceId !== this.loadedWorkspace) this.reset();
    this.loadedWorkspace = workspaceId;
    const sequence = ++this.sequence;
    const base = `/api/workspaces/${encodeURIComponent(workspaceId)}`;
    this.http.get<WorkspaceQuotaAdmission>(`${base}/quota`).subscribe({
      next: quota => {
        if (sequence !== this.sequence) return;
        this.workspaceQuota.set(quota);
        this.setWorkspace(quota.codingAllowed ? null : quota.message);
      },
      error: () => { if (sequence === this.sequence) this.workspaceQuota.set(null); },
    });
    for (const project of projects) {
      this.http.get<WorkspaceQuotaAdmission>(`${base}/projects/${encodeURIComponent(project.id)}/quota`).subscribe({
        next: quota => { if (sequence === this.sequence) this.setProject(project.displayName, quota.codingAllowed ? null : quota.message); },
        error: () => { if (sequence === this.sequence) this.setProject(project.displayName, null); },
      });
    }
  }

  setWorkspace(message: string | null): void { this.workspaceMessage.set(message); }
  setProject(projectName: string, message: string | null): void {
    this.projectMessages.update(current => {
      const next = { ...current };
      if (message) next[projectName] = message;
      else delete next[projectName];
      return next;
    });
  }

  messageFor(projectName: string): string | null {
    return this.projectMessages()[projectName] ?? this.workspaceMessage();
  }
}
