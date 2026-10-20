import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, interval } from 'rxjs';
import type { TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import type { CardDeploymentView, CardEnvironmentBadge } from '../models/card-deployment.model';

@Injectable({ providedIn: 'root' })
export class CardDeploymentService {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  private readonly byCard = signal<ReadonlyMap<string, CardDeploymentView>>(new Map());
  private initialRequested = false;
  private observed = false;
  private inFlight = false;
  private pending = false;

  constructor() {
    this.tasks.taskEvents?.pipe(takeUntilDestroyed()).subscribe(() => {
      if (this.observed && this.hasTasks()) this.refresh();
    });
    // Deployments can change without a task event. Reconcile the badges on the
    // same 30-second fallback cadence as the board while the board is active.
    interval(30_000).pipe(takeUntilDestroyed()).subscribe(() => {
      if (this.observed && this.hasTasks()) this.refresh();
    });
  }

  refresh(): void {
    this.initialRequested = true;
    if (this.inFlight) {
      this.pending = true;
      return;
    }
    this.inFlight = true;
    this.http.get<CardDeploymentView[]>('/api/deploy/cards').pipe(finalize(() => {
      this.inFlight = false;
      if (this.pending) {
        this.pending = false;
        this.refresh();
      }
    })).subscribe({
      next: views => this.byCard.set(new Map(views.map(view => [this.ref(view.projectId, view.cardKey), view]))),
      error: () => { this.initialRequested = false; },
    });
  }

  badges(task: Pick<TaskInfo, 'projectName' | 'key' | 'id'>): CardEnvironmentBadge[] {
    // The service also feeds board filters in views that do not show badges.
    // Start the first read only when a badge is actually observed. Reading jobs
    // here tracks the board's task signal, so newly loaded cards retry this path.
    this.observed = true;
    if (this.hasTasks() && !this.initialRequested) {
      this.initialRequested = true;
      queueMicrotask(() => this.refresh());
    }
    return this.byCard().get(this.ref(task.projectName, task.key || task.id))?.environments ?? [];
  }

  inEnvironment(task: Pick<TaskInfo, 'projectName' | 'key' | 'id'>, kind: 'staging' | 'production'): boolean {
    return this.badges(task).some(badge => badge.kind === kind && badge.state === 'succeeded');
  }

  private ref(project: string, key: string): string { return `${project.toLowerCase()}::${key.toLowerCase()}`; }

  private hasTasks(): boolean { return typeof this.tasks.jobs === 'function' && this.tasks.jobs().length > 0; }
}
