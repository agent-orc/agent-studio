import { Injectable, inject, signal } from '@angular/core';
import { TaskService } from '../../../services/task.service';

/**
 * Owns the visibility of the "+ Add workspace" modal plus the
 * "after-create / after-delete" refresh of the registry workspace list.
 * The dialog component and the per-project Delete affordance both call
 * into this service so neither has to reach back into the studio
 * shell to know whether the project picker has the new entry.
 *
 * Kept as a tiny singleton: callers signal intent via `openCreate()`
 * / `closeCreate()` / `refreshAndClose()`; readers bind to the
 * `createOpen` and `knownNames` signals. The known-names signal is
 * filled lazily on `prime()` (called when the shell loads) so the
 * client-side uniqueness check inside the dialog has data to compare
 * against without a second round trip.
 *
 * `registryChanged` is a monotonic counter that bumps after every
 * successful create or delete. The studio-shell watches it via an
 * effect and calls `reloadRegistryWorkspaces()` so the sidebar tree
 * reflects the mutation without a manual page reload.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceManagerService {
  private readonly jobService = inject(TaskService);

  readonly createOpen = signal(false);
  readonly onboardProjectOpen = signal(false);
  readonly onboardWorkspaceId = signal<string | null>(null);
  readonly knownNames = signal<readonly string[]>([]);
  readonly registryChanged = signal(0);
  readonly projectRenamed = signal<{ previousName: string; currentName: string; revision: number } | null>(null);

  /** Loads the current registry workspace names into `knownNames`.
   *  Safe to call repeatedly; failures are silent so a transient
   *  backend hiccup doesn't block the dialog from opening. */
  prime(): void {
    this.jobService.getRegistryWorkspaces().subscribe({
      next: (entries) => {
        this.knownNames.set((entries ?? []).map(e => e.displayName));
      },
      error: () => { /* leave previous snapshot */ },
    });
  }

  openCreate(): void {
    this.prime();
    this.createOpen.set(true);
  }

  closeCreate(): void {
    this.createOpen.set(false);
  }

  openProjectOnboard(workspaceId: string): void {
    this.onboardWorkspaceId.set(workspaceId);
    this.onboardProjectOpen.set(true);
  }

  closeProjectOnboard(): void {
    this.onboardProjectOpen.set(false);
    this.onboardWorkspaceId.set(null);
  }

  /** Called by the dialog after a successful create. Bumps the
   *  registry-changed counter so the studio-shell reloads its tree,
   *  refreshes the known-names snapshot, and closes the dialog. */
  refreshAndClose(): void {
    this.registryChanged.update(n => n + 1);
    this.prime();
    this.jobService.refresh(true);
    this.createOpen.set(false);
  }

  refreshAfterProjectCreate(): void {
    this.registryChanged.update(n => n + 1);
    this.prime();
    this.jobService.refresh(true);
    this.closeProjectOnboard();
  }

  /** Bump the registry-changed counter without touching the dialogs. Used
   *  when a mutation elsewhere (e.g. adding a Project URL in the Project Hub)
   *  must be reflected in the sidebar tree without a page reload. */
  notifyRegistryChanged(): void {
    this.registryChanged.update(n => n + 1);
  }

  /** Broadcast a registry rename so project-keyed Studio tabs can retarget before stale-tab cleanup runs. */
  notifyProjectRenamed(previousName: string, currentName: string): void {
    this.projectRenamed.update((event) => ({
      previousName,
      currentName,
      revision: (event?.revision ?? 0) + 1,
    }));
    this.notifyRegistryChanged();
  }

  /** Mirrors `refreshAndClose` for the delete path: refresh the
   *  known list so a subsequent create dialog reflects the removal. */
  refreshAfterDelete(): void {
    this.registryChanged.update(n => n + 1);
    this.prime();
    this.jobService.refresh(true);
  }
}
