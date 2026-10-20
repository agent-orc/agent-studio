import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import type { ProjectDeploySnapshot, EnvironmentStatus } from '../../services/project-deploy-data.service';

@Component({
  selector: 'app-project-deploy-overview',
  standalone: true,
  imports: [DatePipe, DisclosureMarkerComponent],
  templateUrl: './project-deploy-overview.html',
  styleUrl: '../project-deploy-area/project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployOverviewComponent {
  readonly snapshot = input.required<ProjectDeploySnapshot>();
  readonly openTools = output<void>();
  readonly openReleases = output<void>();
  readonly logEnvironmentId = signal<string | null>(null);

  toggleLog(id: string): void {
    this.logEnvironmentId.update(current => current === id ? null : id);
  }

  age(status: EnvironmentStatus): string {
    const finished = status.currentDeployment?.finishedAtUtc;
    if (!finished) return 'No deployment yet';
    const hours = Math.max(0, Math.floor((Date.now() - Date.parse(finished)) / 3_600_000));
    return hours < 24 ? `${hours}h ago` : `${Math.floor(hours / 24)}d ago`;
  }
}
