import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import type { DeployLifecycleNode, DeployLifecycleRead, DeployPanel } from '../../state/deploy-area.model';

/** Read-only diagram of the project.yml lifecycle: gate, artifact, then each environment phase. */
@Component({
  selector: 'app-project-deploy-pipeline',
  standalone: true,
  imports: [EmptyStateComponent, LoadingSurfaceComponent],
  templateUrl: './project-deploy-pipeline.html',
  styleUrl: './project-deploy-pipeline.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployPipelineComponent {
  readonly panel = input.required<DeployPanel<DeployLifecycleRead>>();

  /** `invalid`, `undeclared`, or the nodes to draw. */
  readonly diagram = computed<'invalid' | 'undeclared' | DeployLifecycleNode[] | null>(() => {
    const panel = this.panel();
    if (panel.status !== 'ready') return null;
    if (!panel.value.valid) return 'invalid';
    const lifecycle = panel.value.lifecycle;
    return lifecycle?.declared && lifecycle.nodes.length ? lifecycle.nodes : 'undeclared';
  });
  readonly nodes = computed(() => {
    const diagram = this.diagram();
    return Array.isArray(diagram) ? diagram : [];
  });

  gateLabel(node: DeployLifecycleNode): string {
    if (node.kind !== 'environment') return node.kind === 'gate' ? 'Build and test' : 'Built once, promoted unchanged';
    if (node.approval.length) return `Approval: ${node.approval.join(' or ')}`;
    return node.automatic ? 'Automatic' : 'Manual';
  }

  emptyItems(node: DeployLifecycleNode): string {
    if (node.kind === 'gate') return 'No build or test commands declared';
    if (node.kind === 'artifact') return 'No artifacts declared';
    return 'No deploy steps for this environment';
  }
}
