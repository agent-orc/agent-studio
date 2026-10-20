import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { ProjectPipelineDefinitionService, type PipelineDefinitionResponse } from '../../services/project-pipeline-definition.service';
import { ProjectPipelineDiagramComponent } from '../project-pipeline-diagram/project-pipeline-diagram';

@Component({
  selector: 'app-project-deploy-pipeline',
  standalone: true,
  imports: [ProjectPipelineDiagramComponent],
  templateUrl: './project-deploy-pipeline.html',
  styleUrl: '../project-deploy-area/project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployPipelineComponent {
  readonly projectName = input.required<string>();
  private readonly definitions = inject(ProjectPipelineDefinitionService);
  readonly definition = signal<PipelineDefinitionResponse | null>(null);
  readonly loadError = signal(false);

  constructor() {
    effect(onCleanup => {
      this.definition.set(null);
      this.loadError.set(false);
      const subscription = this.definitions.read(this.projectName()).subscribe({
        next: response => this.definition.set(response),
        error: () => this.loadError.set(true),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }
}
