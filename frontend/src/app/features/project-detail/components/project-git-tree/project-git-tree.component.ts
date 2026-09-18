import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import {
  branchCategoryLabel,
  type GitActiveCheckout,
  type GitBranchEntry,
  type GitTaskBadge,
  type GitTreeGroup,
  type GitWorktreeEntry,
} from '../../../git';
import { compactGitRef } from '../../../git/models/git-ref-label';

@Component({
  selector: 'app-project-git-tree',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-git-tree.component.html',
  styleUrl: './project-git-tree.component.scss',
})
export class ProjectGitTreeComponent {
  private readonly taskNavigation = inject(TaskReferenceNavigationService);

  readonly groups = input.required<readonly GitTreeGroup[]>();
  readonly selectedId = input<string | null>(null);

  readonly branchSelected = output<GitBranchEntry>();
  readonly worktreeSelected = output<GitWorktreeEntry>();
  readonly activeSelected = output<GitActiveCheckout>();
  readonly compactRef = compactGitRef;

  categoryLabel(branch: GitBranchEntry): string {
    return branchCategoryLabel(branch.category);
  }

  aheadBehind(branch: GitBranchEntry): string {
    const values: string[] = [];
    if (branch.ahead > 0) values.push(`↑${branch.ahead}`);
    if (branch.behind > 0) values.push(`↓${branch.behind}`);
    return values.join(' ');
  }

  openTask(event: MouseEvent, task: GitTaskBadge): void {
    event.stopPropagation();
    this.taskNavigation.openTaskKey(task.taskKey);
  }
}
