import { HttpClient } from '@angular/common/http';
import { Injectable, effect, inject, signal } from '@angular/core';
import type { TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import type { CardDeploymentView, CardEnvironmentBadge } from '../models/card-deployment.model';

@Injectable({ providedIn: 'root' })
export class CardDeploymentService {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  private readonly byCard = signal<ReadonlyMap<string, CardDeploymentView>>(new Map());
  private requested = false;

  constructor() {
    effect(() => {
      if (this.tasks.jobs().length && !this.requested) this.refresh();
    });
  }

  refresh(): void {
    this.requested = true;
    this.http.get<CardDeploymentView[]>('/api/deploy/cards').subscribe({
      next: views => this.byCard.set(new Map(views.map(view => [this.ref(view.projectId, view.cardKey), view]))),
      error: () => { this.requested = false; },
    });
  }

  badges(task: Pick<TaskInfo, 'projectName' | 'key' | 'id'>): CardEnvironmentBadge[] {
    return this.byCard().get(this.ref(task.projectName, task.key || task.id))?.environments ?? [];
  }

  inEnvironment(task: Pick<TaskInfo, 'projectName' | 'key' | 'id'>, kind: 'staging' | 'production'): boolean {
    return this.badges(task).some(badge => badge.kind === kind && badge.state === 'succeeded');
  }

  private ref(project: string, key: string): string { return `${project.toLowerCase()}::${key.toLowerCase()}`; }
}
