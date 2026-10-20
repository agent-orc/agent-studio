import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import type { ProjectDeploySnapshot, Release } from '../../services/project-deploy-data.service';

@Component({
  selector: 'app-project-deploy-releases',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './project-deploy-releases.html',
  styleUrl: '../project-deploy-area/project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployReleasesComponent {
  readonly snapshot = input.required<ProjectDeploySnapshot>();
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed<Release | null>(() => this.snapshot().releases.find(r => r.id === this.selectedId()) ?? this.snapshot().releases[0] ?? null);
  select(id: string): void { this.selectedId.set(id); }
}
