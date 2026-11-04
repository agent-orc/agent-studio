import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { finalize } from 'rxjs';

export type QualityStudioState = 'not-connected' | 'unreachable' | 'version-too-old' | 'not-registered' | 'ready';
export interface QualityStudioStatus {
  state: QualityStudioState;
  configured: boolean;
  owner: boolean;
  repositoryId: string | null;
}

@Injectable({ providedIn: 'root' })
export class QualityStudioService {
  private readonly http = inject(HttpClient);
  readonly statuses = signal<Record<string, QualityStudioStatus>>({});
  private readonly pending = new Set<string>();

  status(project: string): QualityStudioStatus | undefined { return this.statuses()[project]; }

  ensure(project: string): void {
    if (this.status(project) || this.pending.has(project)) return;
    this.refresh(project);
  }

  refresh(project: string): void {
    if (this.pending.has(project)) return;
    this.pending.add(project);
    this.http.get<QualityStudioStatus>('/api/integrations/quality-studio/status', { params: { project } })
      .pipe(finalize(() => this.pending.delete(project))).subscribe({
      next: status => this.statuses.update(current => ({ ...current, [project]: status })),
      error: () => this.statuses.update(current => ({ ...current, [project]: {
        state: 'unreachable', configured: false, owner: false, repositoryId: null,
      } })),
    });
  }

  open(project: string): void {
    window.open('/api/integrations/quality-studio/open?project=' + encodeURIComponent(project), '_blank', 'noopener');
  }

  openRange(project: string, repositoryId: string, base: string, head: string, card: string): void {
    const target = '/?repo=' + encodeURIComponent(repositoryId)
      + '&view=change&base=' + encodeURIComponent(base)
      + '&head=' + encodeURIComponent(head)
      + '&card=' + encodeURIComponent(card);
    window.open('/api/integrations/quality-studio/open?project=' + encodeURIComponent(project)
      + '&return=' + encodeURIComponent(target), '_blank', 'noopener');
  }

  connectionChanged(): void {
    const projects = Object.keys(this.statuses());
    this.statuses.set({});
    for (const project of projects) this.refresh(project);
  }

  setRepository(project: string, repositoryId: string, done: (error?: string) => void): void {
    this.http.put<void>('/api/integrations/quality-studio/projects/' + encodeURIComponent(project) + '/repository',
      { repositoryId }).subscribe({
      next: () => { this.statuses.update(current => { const copy = { ...current }; delete copy[project]; return copy; }); this.refresh(project); done(); },
      error: () => done('Could not save the repository mapping.'),
    });
  }
}
