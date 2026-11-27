import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/index';
import {
  DeployStepsEnvironmentInput,
  DeployStepsInput,
  PipelineDefinitionIssue,
  PipelineMigrationConfirmResponse,
  PipelineMigrationResponse,
  ProjectPipelineDefinitionService,
} from '../../services/project-pipeline-definition.service';
import { environmentLabel } from '../../state/deploy-area.model';
import { ProjectPipelineDiagramComponent } from '../project-pipeline-diagram/project-pipeline-diagram';

export const DEFAULT_HEALTH_TIMEOUT_SECONDS = 120;

type EnvironmentField = 'target' | 'directory' | 'composeFile' | 'service' | 'healthUrl';

/**
 * Deploy-step form for a schema 2 project.yml that declares environments but
 * no deploy steps (F5). The operator fills the artifact and one step per
 * environment, previews the exact project.yml addition with its validation,
 * and only then writes it as one managed commit.
 */
@Component({
  selector: 'app-project-deploy-steps-form',
  standalone: true,
  imports: [PendingButtonDirective, ProjectPipelineDiagramComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-deploy-steps-form.html',
  styleUrl: './project-deploy-steps-form.scss',
})
export class ProjectDeployStepsFormComponent {
  readonly label = environmentLabel;
  readonly projectName = input.required<string>();
  /** project.yml environment names in lifecycle order. */
  readonly environmentNames = input.required<readonly string[]>();
  readonly written = output<PipelineMigrationConfirmResponse>();
  readonly cancelled = output<void>();

  private readonly definitions = inject(ProjectPipelineDefinitionService);
  readonly artifactType = signal<'static' | 'container'>('static');
  readonly artifactPath = signal('dist');
  readonly environments = signal<DeployStepsEnvironmentInput[]>([]);
  readonly preview = signal<PipelineMigrationResponse | null>(null);
  readonly previewing = signal(false);
  readonly confirming = signal(false);
  readonly error = signal<string | null>(null);

  readonly complete = computed(() => {
    const container = this.artifactType() === 'container';
    return this.artifactPath().trim() !== '' && this.environments().every(item =>
      item.target.trim() !== '' && item.healthUrl.trim() !== '' && !!item.healthTimeoutSeconds
      && (container ? !!item.composeFile?.trim() && !!item.service?.trim() : !!item.directory?.trim()));
  });

  constructor() {
    effect(() => {
      const names = this.environmentNames();
      this.environments.set(names.map(name => ({
        name, target: '', directory: '', composeFile: '', service: '', healthUrl: '',
        healthTimeoutSeconds: DEFAULT_HEALTH_TIMEOUT_SECONDS,
      })));
    });
  }

  setArtifactType(type: 'static' | 'container'): void {
    if (type === this.artifactType()) return;
    this.artifactType.set(type);
    this.artifactPath.set(type === 'static' ? 'dist' : 'Dockerfile');
    this.preview.set(null);
  }

  setArtifactPath(value: string): void {
    this.artifactPath.set(value);
    this.preview.set(null);
  }

  setField(name: string, field: EnvironmentField, value: string): void {
    this.update(name, item => ({ ...item, [field]: value }));
  }

  setHealthTimeout(name: string, value: string): void {
    const seconds = Number.parseInt(value, 10);
    this.update(name, item => ({ ...item, healthTimeoutSeconds: Number.isFinite(seconds) ? seconds : null }));
  }

  previewChange(): void {
    this.previewing.set(true);
    this.error.set(null);
    this.definitions.previewDeploySteps(this.projectName(), this.input()).subscribe({
      next: response => {
        this.preview.set(response);
        this.previewing.set(false);
      },
      error: response => {
        this.error.set(response?.error?.error ?? 'The deploy steps could not be previewed.');
        this.previewing.set(false);
      },
    });
  }

  confirm(): void {
    const preview = this.preview();
    if (!preview?.available || !preview.valid) return;
    this.confirming.set(true);
    this.error.set(null);
    this.definitions.confirmDeploySteps(this.projectName(), this.input(), preview).subscribe({
      next: response => {
        this.confirming.set(false);
        this.preview.set(null);
        this.written.emit(response);
      },
      error: response => {
        this.error.set(response?.error?.error ?? 'The deploy steps could not be written.');
        this.confirming.set(false);
      },
    });
  }

  editForm(): void {
    this.preview.set(null);
  }

  issueKey(issue: PipelineDefinitionIssue): string {
    return `${issue.path}:${issue.code}:${issue.line ?? ''}`;
  }

  private update(name: string, change: (item: DeployStepsEnvironmentInput) => DeployStepsEnvironmentInput): void {
    this.environments.update(items => items.map(item => item.name === name ? change(item) : item));
    this.preview.set(null);
  }

  private input(): DeployStepsInput {
    const container = this.artifactType() === 'container';
    return {
      artifactType: this.artifactType(),
      artifactPath: this.artifactPath().trim(),
      environments: this.environments().map(item => ({
        name: item.name,
        target: item.target.trim(),
        directory: container ? null : item.directory?.trim() ?? null,
        composeFile: container ? item.composeFile?.trim() ?? null : null,
        service: container ? item.service?.trim() ?? null : null,
        healthUrl: item.healthUrl.trim(),
        healthTimeoutSeconds: item.healthTimeoutSeconds,
      })),
    };
  }
}
