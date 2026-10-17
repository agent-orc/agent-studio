import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { ProjectDeploySnapshot } from '../../services/project-deploy-data.service';

@Component({
  selector: 'app-project-deploy-pipeline',
  standalone: true,
  templateUrl: './project-deploy-pipeline.html',
  styleUrl: '../project-deploy-area/project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployPipelineComponent {
  readonly snapshot = input.required<ProjectDeploySnapshot>();
}
