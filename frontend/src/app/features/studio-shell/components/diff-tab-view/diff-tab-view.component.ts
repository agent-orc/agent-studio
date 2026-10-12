import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { retry, timeout } from 'rxjs';
import { TaskService } from '../../../../services/task.service';
import { RowComponent } from '../../../../components/row/row.component';
import { DiffContentComponent } from '../../../../components/diff-content/diff-content.component';
import type { TaskInfo } from '../../../../models/task.model';
import type { GitFileChange } from '../../../git';
import { describeDiffSize, isLargeDiff } from '../../../../utils/large-diff-gate';

interface CommitDiffPayload {
  readonly diff?: string | null;
  readonly emptyReason?: string | null;
}

/**
 * Full-screen "Diff" tab. Resolves the owning job for a commit SHA by
 * walking the live job index — when found, surfaces the project +
 * commit metadata and an "Open task" CTA so the user can jump to the
 * existing in-task diff pane. The inline diff renderer is a follow-up;
 * this view is the shell for it and keeps the tab kind productive
 * (commit SHA + file count + author) instead of dead.
 *
 * The commit message renders collapsed-by-default: only the first line
 * (the subject) is visible at rest, dimmed expander caret on the right.
 * Click toggles full body. Operator feedback 2026-05-22 — a 5-line
 * message block was crowding the meta-grid + diff renderer.
 */
@Component({
  selector: 'app-studio-diff-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RowComponent, DiffContentComponent],
  templateUrl: './diff-tab-view.component.html',
  styleUrl: './diff-tab-view.component.scss',
})
export class StudioDiffViewComponent {
  private readonly jobService = inject(TaskService);

  readonly commitSha = input.required<string>();

  /** Commit-message expander state. Collapsed = first line + ellipsis. */
  readonly messageExpanded = signal(false);
  readonly files = signal<GitFileChange[]>([]);
  readonly filesState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly filesError = signal<string | null>(null);
  readonly selectedPath = signal<string | null>(null);
  readonly diffText = signal('');
  readonly diffState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly diffError = signal<string | null>(null);
  readonly diffEmptyMessage = signal<string | null>(null);
  private readonly revealedPaths = signal<Set<string>>(new Set<string>());
  readonly revealAllLargeDiffs = signal(false);
  private ownerLoadKey = '';
  private diffLoadKey = '';
  private readonly remoteOwner = signal<{ job: TaskInfo; commit: { sha: string; message?: string; filesChanged?: number; at?: string } } | null>(null);

  toggleMessage(): void {
    this.messageExpanded.update((v) => !v);
  }

  /** True when the message body has more than one non-empty line. */
  hasMoreLines(message: string | undefined): boolean {
    if (!message) return false;
    const lines = message.split(/\r?\n/);
    if (lines.length <= 1) return false;
    return lines.slice(1).some((l) => l.trim().length > 0);
  }

  private readonly localOwner = computed<{ job: TaskInfo; commit: { sha: string; message?: string; filesChanged?: number; at?: string } } | null>(() => {
    const sha = this.commitSha();
    const short = sha.slice(0, 7);
    const jobs = this.jobService.jobs();
    for (const job of jobs) {
      const commits = job.commits ?? [];
      for (const c of commits) {
        if (c.sha === sha || c.sha?.startsWith(short)) {
          return {
            job,
            commit: {
              sha: c.sha ?? sha,
              message: c.message,
              filesChanged: c.filesChanged ?? undefined,
              at: c.at,
            },
          };
        }
      }
      if (job.commit && (job.commit.sha === sha || job.commit.sha?.startsWith(short))) {
        return {
          job,
          commit: {
            sha: job.commit.sha ?? sha,
            message: job.commit.message,
            filesChanged: job.commit.filesChanged ?? undefined,
            at: job.commit.at,
          },
        };
      }
    }
    return null;
  });

  readonly owner = computed(() => this.localOwner() ?? this.remoteOwner());

  private readonly _lookupOwner = effect((onCleanup) => {
    const sha = this.commitSha();
    if (this.localOwner()) {
      this.remoteOwner.set(null);
      return;
    }
    this.remoteOwner.set(null);
    // Full commit chains live on task detail. Resolve an older commit only
    // when its diff tab opens, using the cached task index on the server.
    const request = this.jobService.findCommitOwner(sha).subscribe({
      next: match => this.remoteOwner.set({ job: match.job, commit: match.commit }),
      error: () => this.remoteOwner.set(null),
    });
    onCleanup(() => request.unsubscribe());
  });

  readonly selectedFile = computed<GitFileChange | null>(() => {
    const path = this.selectedPath();
    if (!path) return null;
    return this.files().find((f) => f.path === path) ?? null;
  });

  readonly diffIsLarge = computed<boolean>(() => isLargeDiff(this.diffText()));
  readonly diffSizeLabel = computed<string>(() => describeDiffSize(this.diffText()));
  readonly diffGated = computed<boolean>(() => {
    if (!this.diffIsLarge()) return false;
    if (this.revealAllLargeDiffs()) return false;
    const path = this.selectedPath();
    return !(path && this.revealedPaths().has(path));
  });

  private readonly _loadFilesForOwner = effect(() => {
    const owner = this.owner();
    if (!owner) {
      this.ownerLoadKey = '';
      this.resetFiles();
      return;
    }
    const key = `${owner.job.id}|${owner.job.watchPath ?? ''}|${owner.commit.sha}`;
    if (key === this.ownerLoadKey) return;
    this.ownerLoadKey = key;
    this.loadFiles(owner, key);
  });

  selectFile(path: string): void {
    if (this.selectedPath() === path) return;
    this.selectedPath.set(path);
    const owner = this.owner();
    if (owner) this.loadDiff(owner, path);
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
    const owner = this.owner();
    const path = this.selectedPath();
    if (!owner || !path) return;
    this.loadDiff(owner, path);
  }

  trackFile(_index: number, file: GitFileChange): string {
    return file.path;
  }

  private loadFiles(
    owner: { job: TaskInfo; commit: { sha: string } },
    requestKey: string,
  ): void {
    this.filesState.set('loading');
    this.filesError.set(null);
    this.resetDiff();
    this.jobService.getJobCommitFilesBySha(owner.job.id, owner.commit.sha, owner.job.watchPath).subscribe({
      next: (resp) => {
        if (requestKey !== this.ownerLoadKey) return;
        const files = resp.files ?? [];
        this.files.set(files);
        this.filesState.set('loaded');
        const first = files[0]?.path ?? null;
        this.selectedPath.set(first);
        if (first) this.loadDiff(owner, first);
      },
      error: (err) => {
        if (requestKey !== this.ownerLoadKey) return;
        this.files.set([]);
        this.selectedPath.set(null);
        this.filesError.set(err?.error?.error || err?.message || 'Could not load commit files.');
        this.filesState.set('error');
      },
    });
  }

  private loadDiff(owner: { job: TaskInfo; commit: { sha: string } }, path: string): void {
    const requestKey = `${this.ownerLoadKey}|${path}`;
    this.diffLoadKey = requestKey;
    this.diffState.set('loading');
    this.diffError.set(null);
    this.diffEmptyMessage.set(null);
    this.diffText.set('');
    this.jobService.getJobCommitDiffBySha(owner.job.id, owner.commit.sha, path, owner.job.watchPath)
      .pipe(
        timeout({ each: 15000 }),
        retry({ count: 1, delay: 500 }),
      )
      .subscribe({
        next: (resp) => {
          if (requestKey !== this.diffLoadKey) return;
          const payload = this.normalizeDiffPayload(resp);
          this.diffText.set(payload.diff);
          this.diffEmptyMessage.set(
            payload.diff.trim().length > 0
              ? null
              : payload.emptyReason ?? 'No diff for this path in the selected commit.',
          );
          this.diffState.set('loaded');
        },
        error: (err) => {
          if (requestKey !== this.diffLoadKey) return;
          this.diffError.set(this.describeDiffError(err));
          this.diffState.set('error');
        },
      });
  }

  private normalizeDiffPayload(resp: unknown): { diff: string; emptyReason: string | null } {
    if (typeof resp === 'string') {
      return { diff: resp, emptyReason: null };
    }
    if (resp && typeof resp === 'object') {
      const payload = resp as CommitDiffPayload;
      return {
        diff: typeof payload.diff === 'string' ? payload.diff : '',
        emptyReason: typeof payload.emptyReason === 'string' ? payload.emptyReason : null,
      };
    }
    return { diff: '', emptyReason: 'Diff endpoint returned an empty response.' };
  }

  private describeDiffError(err: unknown): string {
    const record = err as { name?: string; message?: string; error?: unknown } | null;
    if (record?.name === 'TimeoutError') {
      return 'Diff request timed out. Retry the file or pick another file.';
    }
    const body = record?.error;
    if (body && typeof body === 'object' && 'error' in body) {
      const message = (body as { error?: unknown }).error;
      if (typeof message === 'string' && message.trim()) return message;
    }
    if (typeof body === 'string' && body.trim()) return body;
    return record?.message || 'Could not load diff.';
  }

  private resetFiles(): void {
    this.files.set([]);
    this.filesState.set('idle');
    this.filesError.set(null);
    this.selectedPath.set(null);
    this.resetDiff();
  }

  private resetDiff(): void {
    this.diffLoadKey = '';
    this.diffText.set('');
    this.diffState.set('idle');
    this.diffError.set(null);
    this.diffEmptyMessage.set(null);
  }
}
