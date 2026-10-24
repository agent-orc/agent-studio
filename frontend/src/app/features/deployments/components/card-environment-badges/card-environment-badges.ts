import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { CardDeploymentService } from '../../services/card-deployment.service';

@Component({
  selector: 'app-card-environment-badges',
  standalone: true,
  templateUrl: './card-environment-badges.html',
  styleUrl: './card-environment-badges.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CardEnvironmentBadgesComponent {
  private readonly deploy = inject(CardDeploymentService);
  readonly task = input.required<TaskInfo>();
  readonly badges = computed(() => this.deploy.badges(this.task()));

  constructor() { this.deploy.observe(); }

  label(name: string, kind: string, state: string): string {
    if (state === 'not-deployed') return kind === 'production' ? 'Not yet in Production' : 'Staging pending';
    if (state === 'rolled-back') return `${name} · rolled back`;
    return `${name} · ${state === 'succeeded' ? 'live' : state}`;
  }
}
