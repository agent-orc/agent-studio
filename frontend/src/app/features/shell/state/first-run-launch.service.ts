import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { AuthService } from '../../../services/auth.service';
import { WorkspaceOverlaysService } from './workspace-overlays.service';
import { ShellAccessService } from '../../../services/shell-access.service';

/** Offers first-run guidance after authentication without replacing a deep link. */
@Injectable({ providedIn: 'root' })
export class FirstRunLaunchService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  private readonly shellAccess = inject(ShellAccessService);

  maybeOpen(open: () => void): void {
    if (!this.shellAccess.operator()) return;
    if (this.auth.status()?.profile === 'public-demo-readonly') return;
    this.http.get<{ id: string; archived: boolean }[]>('/api/v1/projects').subscribe({
      next: projects => {
        if ((projects ?? []).some(project => !project.archived) || this.overlays.settingsOpen()) return;
        if (window.location.hash && !['#/board', '#/'].includes(window.location.hash)) return;
        open();
      },
      error: () => undefined,
    });
  }
}
