import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import type { RegistryProjectUrl } from '../../../models/task.model';

/** A Project URL record resolved together with its owning registry project id. */
export interface ResolvedProjectUrl {
  projectId: string;
  watchPath: string;
  url: RegistryProjectUrl;
  repositoryPath: string | null;
  rootPath: string | null;
}

/**
 * AGT-2067 — resolve a Project URL record by project display name + url id.
 *
 * Both the Project Hub "Project URLs" management panel and the embedded
 * preview tab need the same "find this URL on this project" lookup against the
 * registry (`GET /api/workspaces` → project → urls). Centralising it here keeps
 * the two surfaces from re-deriving the same walk and gives the preview tab a
 * single seam to detect "this URL was removed from the project" (a `null`
 * result) rather than a stuck spinner.
 */
@Injectable({ providedIn: 'root' })
export class ProjectUrlLookupService {
  private readonly taskService = inject(TaskService);

  /**
   * Resolve `{ projectId, url }` for a project's URL, or `null` when the
   * project or the URL id is not (or no longer) in the registry.
   */
  resolve(projectName: string, urlId: string): Observable<ResolvedProjectUrl | null> {
    return this.taskService.getRegistryWorkspaces().pipe(
      map(workspaces => {
        for (const ws of workspaces ?? []) {
          for (const project of ws.projects) {
            if (project.displayName !== projectName) continue;
            const url = (project.urls ?? []).find(u => u.id === urlId);
            if (url) return {
              projectId: project.id,
              watchPath: project.storageLocation,
              url,
              repositoryPath: project.repositoryPath,
              rootPath: project.rootPath,
            };
          }
        }
        return null;
      }),
    );
  }
}
