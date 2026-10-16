import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class WorkspaceQuotaBoardState {
  private readonly workspaceMessage = signal<string | null>(null);
  private readonly projectMessages = signal<Record<string, string>>({});

  reset(): void {
    this.workspaceMessage.set(null);
    this.projectMessages.set({});
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
