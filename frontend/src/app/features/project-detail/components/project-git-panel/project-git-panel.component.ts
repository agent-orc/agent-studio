import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ProjectGitService } from '../../../../services/project-git.service';
import {
  buildGitTree,
  type GitActiveCheckout,
  type GitBranchEntry,
  type GitGraphCommit,
  type GitProjectInventory,
  type GitWorktreeEntry,
} from '../../../git';
import { ProjectBranchSweepComponent } from '../project-branch-sweep/project-branch-sweep.component';
import { ProjectGitChangesComponent } from '../project-git-changes/project-git-changes.component';
import { ProjectGitHistoryComponent } from '../project-git-history/project-git-history.component';
import { ProjectGitTreeComponent } from '../project-git-tree/project-git-tree.component';

type LoadState = 'idle' | 'loading' | 'loaded' | 'error';

type GitSelection =
  | { kind: 'branch'; branch: GitBranchEntry }
  | { kind: 'worktree'; worktree: GitWorktreeEntry }
  | { kind: 'active'; checkout: GitActiveCheckout }
  | { kind: 'commit'; commit: GitGraphCommit };

/**
 * Project-scoped, read-only Git graph. The left tree explains refs, worktrees,
 * and active leases while the main surface keeps commit topology and metadata
 * primary. Commit changes are fetched only after an explicit inspect action.
 */
@Component({
  selector: 'app-project-git-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ProjectBranchSweepComponent,
    ProjectGitChangesComponent,
    ProjectGitHistoryComponent,
    ProjectGitTreeComponent,
  ],
  templateUrl: './project-git-panel.component.html',
  styleUrl: './project-git-panel.component.scss',
})
export class ProjectGitPanelComponent {
  private readonly projectGit = inject(ProjectGitService);

  readonly projectName = input.required<string>();
  readonly inventory = signal<GitProjectInventory | null>(null);
  readonly inventoryState = signal<LoadState>('idle');
  readonly inventoryError = signal<string | null>(null);
  readonly commits = signal<GitGraphCommit[]>([]);
  readonly historyHasMore = signal(false);
  readonly historyNextOffset = signal<number | null>(null);
  readonly historyLoading = signal(false);
  readonly historyError = signal<string | null>(null);
  readonly selection = signal<GitSelection | null>(null);
  readonly inspectedCommit = signal<GitGraphCommit | null>(null);

  readonly tree = computed(() => buildGitTree(this.inventory()));
  readonly showEmpty = computed(() => {
    if (this.inventoryState() === 'error') return true;
    const inventory = this.inventory();
    return !!inventory && !inventory.isRepo;
  });
  readonly selectedId = computed<string | null>(() => {
    const selection = this.selection();
    if (!selection) return null;
    if (selection.kind === 'branch') return `branch:${selection.branch.name}`;
    if (selection.kind === 'worktree') return `wt:${selection.worktree.path}`;
    if (selection.kind === 'active') return `active:${selection.checkout.task.taskKey}`;
    return null;
  });
  readonly selectedCommitSha = computed<string | null>(() => {
    const selection = this.selection();
    if (!selection) return null;
    if (selection.kind === 'branch') return selection.branch.tipSha;
    if (selection.kind === 'worktree') return selection.worktree.headSha;
    if (selection.kind === 'active') return selection.checkout.headSha;
    return selection.commit.sha;
  });

  constructor() {
    effect(() => {
      const project = this.projectName();
      this.selection.set(null);
      this.inspectedCommit.set(null);
      this.loadInventory(project);
    });
  }

  refresh(): void {
    this.loadInventory(this.projectName());
  }

  selectBranch(branch: GitBranchEntry): void {
    this.selection.set({ kind: 'branch', branch });
  }

  selectWorktree(worktree: GitWorktreeEntry): void {
    this.selection.set({ kind: 'worktree', worktree });
  }

  selectActive(checkout: GitActiveCheckout): void {
    this.selection.set({ kind: 'active', checkout });
  }

  selectCommit(commit: GitGraphCommit): void {
    this.selection.set({ kind: 'commit', commit });
  }

  inspectChanges(commit: GitGraphCommit): void {
    this.selection.set({ kind: 'commit', commit });
    this.inspectedCommit.set(commit);
  }

  closeChanges(): void {
    this.inspectedCommit.set(null);
  }

  loadOlder(): void {
    const offset = this.historyNextOffset();
    if (offset === null || this.historyLoading()) return;
    this.historyLoading.set(true);
    this.historyError.set(null);
    this.projectGit.getHistory(this.projectName(), offset).subscribe({
      next: page => {
        const known = new Set(this.commits().map(commit => commit.sha));
        this.commits.update(current => [
          ...current,
          ...page.commits.filter(commit => !known.has(commit.sha)),
        ]);
        this.historyHasMore.set(page.hasMore);
        this.historyNextOffset.set(page.nextOffset);
        this.historyLoading.set(false);
      },
      error: error => {
        this.historyError.set(this.describeError(error, 'Could not load older commits.'));
        this.historyLoading.set(false);
      },
    });
  }

  private loadInventory(project: string): void {
    this.inventoryState.set('loading');
    this.inventoryError.set(null);
    this.projectGit.getInventory(project).subscribe({
      next: inventory => {
        this.inventory.set(inventory);
        this.commits.set(inventory.history?.commits ?? []);
        this.historyHasMore.set(inventory.history?.hasMore ?? false);
        this.historyNextOffset.set(inventory.history?.nextOffset ?? null);
        this.historyError.set(null);
        this.inventoryState.set('loaded');
        if (!inventory.isRepo) {
          this.inventoryError.set(inventory.error ?? 'This project has no git repository.');
        }
      },
      error: error => {
        this.inventory.set(null);
        this.commits.set([]);
        this.inventoryError.set(this.describeError(error, 'Could not load git inventory.'));
        this.inventoryState.set('error');
      },
    });
  }

  private describeError(error: unknown, fallback: string): string {
    const record = error as { error?: unknown; message?: string } | null;
    const body = record?.error;
    if (body && typeof body === 'object' && 'error' in body) {
      const message = (body as { error?: unknown }).error;
      if (typeof message === 'string' && message.trim()) return message;
    }
    if (typeof body === 'string' && body.trim()) return body;
    return record?.message || fallback;
  }
}
