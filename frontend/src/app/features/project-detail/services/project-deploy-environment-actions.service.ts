import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { switchMap, throwError } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TaskService } from '../../../services/task.service';
import type { EnvironmentCardView } from '../state/deploy-area.model';
import { ProjectDeployDataService, serverMessage } from './project-deploy-data.service';

/** Deploy environment actions keep repository changes separate from host removal. */
@Injectable()
export class ProjectDeployEnvironmentActions {
  private readonly data = inject(ProjectDeployDataService);
  private readonly tasks = inject(TaskService);
  private readonly destroyRef = inject(DestroyRef);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  stop(project: string, environment: EnvironmentCardView | undefined, refresh: () => void): void {
    if (this.busy() || !environment?.artifact) return;
    if (!window.confirm(`Stop ${environment.name}? The app will be taken down after the host acknowledges. The declaration, deploy history, health samples, and environment secrets are kept. Health checks remain declared. Nothing is deleted.`)) return;
    this.busy.set(true);
    this.error.set(null);
    this.data.stopEnvironment(project, environment.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.busy.set(false);
        this.message.set(`Stop queued for ${environment.name}. The app remains up until the host acknowledges.`);
        refresh();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(serverMessage(error) ?? `Could not stop ${environment.name}.`);
      },
    });
  }

  remove(project: string, environment: EnvironmentCardView | undefined): void {
    if (this.busy() || !environment) return;
    if (!window.confirm(`Remove ${environment.name}? This queues a card to delete its repository declaration and related project.yml references. The app stays on its host until a separate confirmation after that change is delivered. Deploy history, health samples, and environment secrets are kept. Open health incidents close as check-removed when checks disappear. No stored samples or secrets are deleted.`)) return;
    this.busy.set(true);
    this.error.set(null);
    this.tasks.getWatchPaths().pipe(switchMap(paths => {
      const path = paths.find(item => item.name.toLowerCase() === project.toLowerCase())?.path;
      if (!path) return throwError(() => new Error(`No watch path found for ${project}.`));
      return this.tasks.createJob({
        title: `Remove ${environment.name} environment`,
        agent: 'codex', cliType: 'codex', watchPath: path, targetState: '1-preparation', taskType: 'chore',
        promptMarkdown: `Remove environment ${environment.id} from project ${project}. Delete its declaration under docs/deployments/environments/ and update .agent-studio/project.yml so all environments, lifecycles, deploy steps, variables, channels, and runbooks remain valid. For Production, a Staging-only schema 2 definition is supported. Do not add a removal tombstone: the app must stay on its host until an operator confirms the pending removal in Deploy. Keep deploy history, health samples, incidents, and environment secrets. Report the exact files changed and validation results.`,
      });
    }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: task => {
        this.busy.set(false);
        this.message.set(`Created ${task.id} to remove ${environment.name}. Confirm host removal here after the declaration change is delivered.`);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(serverMessage(error) ?? (error instanceof Error ? error.message : 'Could not create the removal card.'));
      },
    });
  }

  confirm(project: string, id: string, hostId: string, refresh: () => void): void {
    if (this.busy()) return;
    if (!window.confirm(`Remove ${id} from ${hostId}? The host will delete the running app after its next poll. Deploy history, health samples, and environment secrets are kept. Open health incidents close as check-removed when checks disappear. The repository declaration was already deleted.`)) return;
    this.busy.set(true);
    this.error.set(null);
    this.data.confirmRunTargetRemoval(project, id, hostId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.busy.set(false);
        this.message.set(`Host removal confirmed for ${id} on ${hostId}.`);
        refresh();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(serverMessage(error) ?? `Could not confirm removal of ${id}.`);
      },
    });
  }
}
