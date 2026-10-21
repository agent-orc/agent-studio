import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { ProjectPipelineDiagramComponent } from '../project-pipeline-diagram/project-pipeline-diagram';
import type { PipelineDefinitionResponse } from '../../services/project-pipeline-definition.service';
import type { DeployPanel } from '../../state/deploy-area.model';

/** Read-only view of AGT-3221's parsed project.yml diagram and validation issues. */
@Component({
  selector: 'app-project-deploy-pipeline', standalone: true,
  imports: [EmptyStateComponent, LoadingSurfaceComponent, ProjectPipelineDiagramComponent],
  templateUrl: './project-deploy-pipeline.html', styleUrl: './project-deploy-pipeline.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployPipelineComponent {
  readonly panel = input.required<DeployPanel<PipelineDefinitionResponse>>();
  readonly definition = computed(() => {
    const panel = this.panel();
    return panel.status === 'ready' ? panel.value : null;
  });
  readonly errorMessage = computed(() => {
    const panel = this.panel();
    return panel.status === 'error' ? panel.message : '';
  });
}
