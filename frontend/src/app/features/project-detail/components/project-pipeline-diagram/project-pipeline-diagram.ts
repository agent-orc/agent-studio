import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type {
  PipelineDefinitionIssue,
  PipelineDiagram,
  PipelineStepNode,
} from '../../services/project-pipeline-definition.service';

const STEP_LABELS: Record<string, string> = {
  'static-site': 'Static site over SSH',
  container: 'Compose service over SSH',
  'publish-static': 'Publish to deploy branch',
  'start-container': 'Start container',
  run: 'Run command',
  health: 'Health check',
  approval: 'Approval',
};

/**
 * Draws a parsed project.yml pipeline: gate, then the artifacts it keeps, then
 * each environment in lifecycle order. Issues attach to the node whose
 * definition path they name, so a broken step is visible where it lives.
 */
@Component({
  selector: 'app-project-pipeline-diagram',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-pipeline-diagram.html',
  styleUrl: './project-pipeline-diagram.scss',
})
export class ProjectPipelineDiagramComponent {
  readonly diagram = input.required<PipelineDiagram>();
  readonly issues = input<PipelineDefinitionIssue[]>([]);

  readonly gateCommands = computed(() => {
    const gate = this.diagram().gate;
    return [...gate.build, ...gate.test];
  });

  readonly artifactIssues = computed(() => this.countUnder('artifacts'));

  environmentLabel(name: string): string {
    return name.length === 0 ? name : name[0].toUpperCase() + name.slice(1);
  }

  stepLabel(step: PipelineStepNode): string {
    return STEP_LABELS[step.kind] ?? step.kind;
  }

  issueCount(path: string): number {
    return this.countUnder(path);
  }

  /** Steps and variables are reported under their own paths, so an environment adds them up. */
  environmentIssueCount(path: string, steps: { path: string }[], variables: { path: string }[]): number {
    return this.countUnder(path) + [...steps, ...variables].reduce((sum, node) => sum + this.countUnder(node.path), 0);
  }

  private countUnder(path: string): number {
    return this.issues().filter(issue =>
      issue.path === path || issue.path.startsWith(`${path}.`) || issue.path.startsWith(`${path}[`)).length;
  }
}
