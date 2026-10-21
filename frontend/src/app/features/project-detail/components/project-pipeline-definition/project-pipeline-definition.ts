import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import {
  PipelineDefinitionIssue,
  PipelineDefinitionResponse,
  PipelineMigrationResponse,
  ProjectPipelineDefinitionService,
} from '../../services/project-pipeline-definition.service';
import { ProjectPipelineDiagramComponent } from '../project-pipeline-diagram/project-pipeline-diagram';

/**
 * Settings section for the project.yml pipeline: the parsed diagram, the
 * validation result with line and path, and the schema 1 to schema 2
 * migration. The migration is a dry run first; only the explicit confirmation
 * writes, and the backend refuses it when project.yml changed in between.
 */
@Component({
  selector: 'app-project-pipeline-definition',
  standalone: true,
  imports: [PendingButtonDirective, ProjectPipelineDiagramComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-pipeline-definition.html',
  styleUrl: './project-pipeline-definition.scss',
})
export class ProjectPipelineDefinitionComponent implements OnInit {
  readonly projectName = input.required<string>();

  private readonly definitions = inject(ProjectPipelineDefinitionService);
  readonly loading = signal(true);
  readonly definition = signal<PipelineDefinitionResponse | null>(null);
  readonly migration = signal<PipelineMigrationResponse | null>(null);
  readonly previewing = signal(false);
  readonly confirming = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.definitions.read(this.projectName()).subscribe({
      next: response => {
        // An unexpected body (a proxy page, an empty list) reads as a load failure.
        if (!Array.isArray(response?.issues)) this.error.set('The pipeline definition could not be loaded.');
        this.definition.set(Array.isArray(response?.issues) ? response : null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('The pipeline definition could not be loaded.');
        this.loading.set(false);
      },
    });
  }

  previewMigration(): void {
    this.previewing.set(true);
    this.message.set(null);
    this.error.set(null);
    this.definitions.dryRun(this.projectName()).subscribe({
      next: response => {
        this.migration.set(response);
        this.previewing.set(false);
      },
      error: response => {
        this.error.set(response?.error?.error ?? 'The migration preview could not be created.');
        this.previewing.set(false);
      },
    });
  }

  confirmMigration(): void {
    const migration = this.migration();
    if (!migration?.available || !migration.valid) return;
    this.confirming.set(true);
    this.message.set(null);
    this.error.set(null);
    this.definitions.confirm(this.projectName(), migration).subscribe({
      next: response => {
        this.definition.set(response.definition);
        this.migration.set(null);
        this.message.set(response.commitSha
          ? `project.yml now uses schema 2 (commit ${response.commitSha.slice(0, 7)}).`
          : 'project.yml already matched the proposal.');
        this.confirming.set(false);
      },
      error: response => {
        this.error.set(response?.error?.error ?? 'The migration could not be written.');
        this.confirming.set(false);
      },
    });
  }

  discardMigration(): void {
    this.migration.set(null);
  }

  issueKey(issue: PipelineDefinitionIssue): string {
    return `${issue.path}:${issue.code}:${issue.line ?? ''}`;
  }
}
