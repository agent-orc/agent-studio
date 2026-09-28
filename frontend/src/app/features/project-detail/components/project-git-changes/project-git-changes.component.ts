import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { DiffContentComponent } from '../../../../components/diff-content/diff-content.component';
import { ProjectGitService } from '../../../../services/project-git.service';
import { describeDiffSize, isLargeDiff } from '../../../../utils/large-diff-gate';
import type { GitFileChange, GitGraphCommit } from '../../../git';

type LoadState = 'idle' | 'loading' | 'loaded' | 'error';

/** Lazy, optional changed-file and diff inspector for one graph commit. */
@Component({
  selector: 'app-project-git-changes',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DiffContentComponent],
  templateUrl: './project-git-changes.component.html',
  styleUrl: './project-git-changes.component.scss',
})
export class ProjectGitChangesComponent {
  private readonly projectGit = inject(ProjectGitService);

  readonly projectName = input.required<string>();
  readonly commit = input.required<GitGraphCommit>();
  readonly initialPath = input<string | null>(null);
  readonly closed = output<void>();

  readonly files = signal<GitFileChange[]>([]);
  readonly filesState = signal<LoadState>('idle');
  readonly filesError = signal<string | null>(null);
  readonly selectedPath = signal<string | null>(null);
  readonly diffText = signal('');
  readonly diffState = signal<LoadState>('idle');
  readonly diffError = signal<string | null>(null);
  readonly diffEmptyMessage = signal<string | null>(null);
  readonly revealAllLargeDiffs = signal(false);
  private readonly revealedPaths = signal<Set<string>>(new Set<string>());
  private filesLoadKey = '';
  private diffLoadKey = '';

  readonly selectedFile = computed(() => {
    const path = this.selectedPath();
    return path ? this.files().find(file => file.path === path) ?? null : null;
  });
  readonly diffIsLarge = computed(() => isLargeDiff(this.diffText()));
  readonly diffSizeLabel = computed(() => describeDiffSize(this.diffText()));
  readonly diffGated = computed(() => {
    if (!this.diffIsLarge() || this.revealAllLargeDiffs()) return false;
    const path = this.selectedPath();
    return !(path && this.revealedPaths().has(path));
  });

  constructor() {
    effect(() => {
      const project = this.projectName();
      const commit = this.commit();
      const key = `${project}|${commit.sha}`;
      if (key === this.filesLoadKey) return;
      this.filesLoadKey = key;
      this.reset();
      this.loadFiles(project, commit.sha, key);
    });
  }

  selectFile(path: string): void {
    if (path === this.selectedPath()) return;
    this.selectedPath.set(path);
    this.loadDiff(this.projectName(), this.commit().sha, path);
  }

  revealCurrentDiff(): void {
    const path = this.selectedPath();
    if (!path) return;
    const next = new Set(this.revealedPaths());
    next.add(path);
    this.revealedPaths.set(next);
  }

  revealAll(): void {
    this.revealAllLargeDiffs.set(true);
  }

  retryDiff(): void {
    const path = this.selectedPath();
    if (path) this.loadDiff(this.projectName(), this.commit().sha, path);
  }

  private loadFiles(project: string, sha: string, requestKey: string): void {
    this.filesState.set('loading');
    this.projectGit.getCommitFiles(project, sha).subscribe({
      next: response => {
        if (requestKey !== this.filesLoadKey) return;
        const files = response.files ?? [];
        this.files.set(files);
        this.filesState.set('loaded');
        const requested = this.initialPath();
        const first = files.some(file => file.path === requested) ? requested : files[0]?.path ?? null;
        this.selectedPath.set(first);
        if (first) this.loadDiff(project, sha, first);
      },
      error: error => {
        if (requestKey !== this.filesLoadKey) return;
        this.filesError.set(this.describeError(error, 'Could not load commit files.'));
        this.filesState.set('error');
      },
    });
  }

  private loadDiff(project: string, sha: string, path: string): void {
    const requestKey = `${project}|${sha}|${path}`;
    this.diffLoadKey = requestKey;
    this.diffState.set('loading');
    this.diffError.set(null);
    this.diffEmptyMessage.set(null);
    this.diffText.set('');
    this.projectGit.getCommitDiff(project, sha, path).subscribe({
      next: response => {
        if (requestKey !== this.diffLoadKey) return;
        const diff = typeof response?.diff === 'string' ? response.diff : '';
        this.diffText.set(diff);
        this.diffEmptyMessage.set(
          diff.trim() ? null : response?.emptyReason ?? 'No diff for this path in the selected commit.',
        );
        this.diffState.set('loaded');
      },
      error: error => {
        if (requestKey !== this.diffLoadKey) return;
        this.diffError.set(this.describeError(error, 'Could not load diff.'));
        this.diffState.set('error');
      },
    });
  }

  private reset(): void {
    this.files.set([]);
    this.filesState.set('idle');
    this.filesError.set(null);
    this.selectedPath.set(null);
    this.revealedPaths.set(new Set<string>());
    this.revealAllLargeDiffs.set(false);
    this.diffLoadKey = '';
    this.diffText.set('');
    this.diffState.set('idle');
    this.diffError.set(null);
    this.diffEmptyMessage.set(null);
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
