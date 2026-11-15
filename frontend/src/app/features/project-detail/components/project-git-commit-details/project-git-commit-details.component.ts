import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { ProjectGitService } from '../../../../services/project-git.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { formatCompactDateTime } from '../../../../services/format.util';
import type { GitFileChange, GitGraphCommit, GitTaskBadge } from '../../../git';

/** Integration presence of a commit: integrated, not integrated, or not recorded. */
export function commitIntegrationStatus(
  presence: Pick<NonNullable<GitGraphCommit['presence']>, 'inIntegration' | 'integrationBranch'> | null | undefined,
): { label: string; tone: StatusTone } {
  if (!presence) return { label: 'Not recorded', tone: 'neutral' };
  return presence.inIntegration
    ? { label: `Integrated · ${presence.integrationBranch}`, tone: 'success' }
    : { label: `Not integrated · ${presence.integrationBranch}`, tone: 'neutral' };
}

@Component({
  selector: 'app-project-git-commit-details',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-git-commit-details.component.html',
  styleUrl: './project-git-commit-details.component.scss',
})
export class ProjectGitCommitDetailsComponent {
  readonly integrationStatus = commitIntegrationStatus;
  private readonly git = inject(ProjectGitService);
  private readonly tasks = inject(TaskReferenceNavigationService);
  readonly projectName = input.required<string>();
  readonly commit = input.required<GitGraphCommit>();
  readonly parentSelected = output<string>();
  readonly fileSelected = output<string>();
  readonly closed = output<void>();
  readonly files = signal<GitFileChange[]>([]);
  readonly loading = signal(true);
  readonly copied = signal(false);

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      const sha = this.commit().sha;
      this.loading.set(true);
      this.files.set([]);
      const subscription = this.git.getCommitFiles(project, sha).subscribe({
        next: response => { this.files.set(response.files ?? []); this.loading.set(false); },
        error: () => this.loading.set(false),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  when(value: string | undefined): string { return value ? formatCompactDateTime(value) : 'Not recorded'; }
  openTask(task: GitTaskBadge): void { this.tasks.openTaskKey(task.taskKey); }
  async copySha(): Promise<void> {
    await navigator.clipboard?.writeText(this.commit().sha);
    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 1400);
  }
}
