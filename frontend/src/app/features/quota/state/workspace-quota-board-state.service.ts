import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

/** Quota status of one scope; `hosting-full` still admits coding, only new deploys wait. */
export type WorkspaceQuotaStatus = 'ok' | 'warning' | 'blocked' | 'hosting-full';

/** Admission view of one quota scope (the workspace, or one of its projects). */
export interface WorkspaceQuotaAdmission {
  projectId: string | null;
  /** Project display name; null for the workspace scope. */
  project: string | null;
  status: WorkspaceQuotaStatus;
  message: string | null;
  overrideActive: boolean;
  codingAllowed: boolean;
  hostingAllowed: boolean;
  /** A limit applies to this scope. */
  limited: boolean;
}

/** `/api/workspaces/{id}/quota/admission`: the workspace and every project in one answer. */
export interface WorkspaceQuotaBoardAdmission {
  workspaceId: string;
  workspace: WorkspaceQuotaAdmission;
  projects: WorkspaceQuotaAdmission[];
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
    this.clear();
  }

  /**
   * Reads the workspace and per-project admission with one request. Switching
   * workspace drops the previous messages at once; a superseded read never lands.
   */
  load(workspaceId: string): void {
    if (workspaceId !== this.loadedWorkspace) this.reset();
    this.loadedWorkspace = workspaceId;
    const sequence = ++this.sequence;
    this.http.get<WorkspaceQuotaBoardAdmission>(`/api/workspaces/${encodeURIComponent(workspaceId)}/quota/admission`).subscribe({
      next: admission => {
        if (sequence !== this.sequence) return;
        this.workspaceQuota.set(admission.workspace);
        this.setWorkspace(refusal(admission.workspace));
        const messages: Record<string, string> = {};
        for (const project of admission.projects) {
          const message = refusal(project);
          if (project.project && message) messages[project.project] = message;
        }
        this.projectMessages.set(messages);
      },
      error: () => { if (sequence === this.sequence) this.clear(); },
    });
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

  private clear(): void {
    this.workspaceQuota.set(null);
    this.workspaceMessage.set(null);
    this.projectMessages.set({});
  }
}

/** Card message of a scope: only a refusal of new coding work is worth a card line. */
function refusal(scope: WorkspaceQuotaAdmission): string | null {
  return scope.codingAllowed ? null : scope.message;
}
