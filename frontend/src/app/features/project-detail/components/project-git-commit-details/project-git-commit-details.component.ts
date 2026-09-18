import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { ProjectGitService } from '../../../../services/project-git.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { formatCompactDateTime } from '../../../../services/format.util';
import type { GitFileChange, GitGraphCommit, GitTaskBadge } from '../../../git';

@Component({
  selector: 'app-project-git-commit-details',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-git-commit-details.component.html',
  styleUrl: './project-git-commit-details.component.scss',
  host: { '(keydown.escape)': 'closed.emit()' },
})
export class ProjectGitCommitDetailsComponent {
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
    effect(() => {
      const project = this.projectName();
      const sha = this.commit().sha;
      this.loading.set(true);
      this.files.set([]);
      this.git.getCommitFiles(project, sha).subscribe({
        next: response => { this.files.set(response.files ?? []); this.loading.set(false); },
        error: () => this.loading.set(false),
      });
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
