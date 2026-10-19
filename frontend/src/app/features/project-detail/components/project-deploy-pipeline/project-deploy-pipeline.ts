import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import type { DeployPanel } from '../../state/deploy-area.model';
import {
  stepKindLabel,
  type DeployPipelineView,
  type PipelineNodeView,
  type PipelineStepView,
} from '../../state/deploy-pipeline.model';

/**
 * Read-only diagram of the project.yml pipeline: gate, artifact, then each
 * environment phase with its steps. An invalid file still shows its parsed
 * diagram, below the validation issues.
 */
@Component({
  selector: 'app-project-deploy-pipeline',
  standalone: true,
  imports: [EmptyStateComponent, LoadingSurfaceComponent],
  templateUrl: './project-deploy-pipeline.html',
  styleUrl: './project-deploy-pipeline.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployPipelineComponent {
  readonly panel = input.required<DeployPanel<DeployPipelineView>>();

  readonly pipeline = computed(() => {
    const panel = this.panel();
    return panel.status === 'ready' ? panel.value : null;
  });
  readonly errorMessage = computed(() => {
    const panel = this.panel();
    return panel.status === 'error' ? `${panel.message} Try Refresh.` : '';
  });

  protected readonly kindLabel = stepKindLabel;

  gateLabel(node: PipelineNodeView): string {
    if (node.kind !== 'environment') return node.kind === 'gate' ? 'Build and test' : 'Built once, promoted unchanged';
    if (node.approval.length) return `Approval: ${node.approval.join(' or ')}`;
    return node.automatic ? 'Automatic' : 'Manual';
  }

  emptyItems(node: PipelineNodeView): string {
    if (node.kind === 'gate') return 'No build or test commands declared';
    if (node.kind === 'artifact') return 'No artifacts declared';
    return 'No deploy steps for this environment';
  }

  /** `web to web-vm`, then where it writes and how health is checked. */
  stepDetails(step: PipelineStepView): string[] {
    const details: string[] = [];
    const route = [step.artifact, step.target].filter(Boolean).join(' to ');
    if (route) details.push(route);
    if (step.destination) details.push(step.destination);
    if (step.health) {
      details.push(step.healthTimeoutSeconds ? `Health ${step.health} within ${step.healthTimeoutSeconds} s` : `Health ${step.health}`);
    }
    return details;
  }
}
