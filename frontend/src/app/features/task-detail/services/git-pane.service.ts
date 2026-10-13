import { Injectable, OnDestroy, computed, signal, inject, untracked } from '@angular/core';
import type { TaskInfo } from '../../../models/task.model';
import type {
  GitFileChange,
  GitStatus,
  TaskCommitDetail,
  TaskCommitInfo,
  TaskProvenanceView,
} from '../../../features/git';
import { TaskService } from '../../../services/task.service';
import type { CodeReviewListEntry } from '../../../services/task.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import {
  setVisibleInterval,
  clearVisibleInterval,
  VisibleIntervalHandle,
} from '../../../utils/visible-interval';

/**
 * Owns the Git pane state and API calls for a single job-detail
 * instance: working-tree status, the currently-selected diff path and
 * its body, the commit-message draft, and the generate/commit progress
 * flags.
 *
 * Provided locally on TaskDetailComponent. The component supplies the
 * current `TaskInfo` (via setJob) and the service drives all backend
 * traffic + signals from there.
 */
@Injectable()
export class GitPaneService implements OnDestroy {
  private jobService = inject(TaskService);
  private errorDialog = inject(ErrorDialogService);

  readonly status = signal<GitStatus | null>(null);
  readonly loading = signal(false);
  readonly selectedDiffPath = signal<string | null>(null);
  readonly diffText = signal<string>('');

  // --- md/html preview (AGT-2008) --------------------------------------
  // The git-pane can render a changed .md/.html file as a formatted preview
  // instead of its diff. Content is fetched lazily the first time the preview
  // is shown for a path, from the same ref the diff came from (working tree,
  // the selected commit, or - in the aggregated view - the newest task
  // commit) so the preview matches what the diff shows.
  readonly previewContent = signal<string | null>(null);
  readonly previewLoading = signal(false);
  readonly previewError = signal<string | null>(null);
  readonly previewIsBinary = signal(false);
  private readonly PREVIEW_CACHE_LIMIT = 16;
  private previewCache = new Map<string, { content: string; isBinary: boolean }>();
  readonly commitMessage = signal('');
  readonly committing = signal(false);
  readonly generatingMsg = signal(false);

  /**
   * Small LRU diff cache keyed by `(mode|sha|path)`. Selecting a file in
   * the tree fires a backend `git diff` / `git show` round-trip; without
   * a cache, clicking back-and-forth across files repeatedly pays the
   * network cost and the diff2html re-render. The cache invalidates
   * whenever `setJob` is called for a different job, when the working
   * tree refreshes (`refresh()` resets the worktree slice), and on
   * commit (history changes). 32 entries keeps memory bounded for the
   * largest realistic change sets.
   */
  private readonly DIFF_CACHE_LIMIT = 32;
  private diffCache = new Map<string, string>();
  private cacheKey(path: string): string {
    if (this.viewMode() === 'commit') {
      const sha = this.selectedCommitSha() ?? '';
      return `commit|${sha}|${path}`;
    }
    return `worktree|${path}`;
  }
  private cacheGet(key: string): string | undefined {
    const v = this.diffCache.get(key);
    if (v !== undefined) {
      // Touch LRU order.
      this.diffCache.delete(key);
      this.diffCache.set(key, v);
    }
    return v;
  }
  private cachePut(key: string, value: string): void {
    if (this.diffCache.has(key)) this.diffCache.delete(key);
    this.diffCache.set(key, value);
    while (this.diffCache.size > this.DIFF_CACHE_LIMIT) {
      const oldest = this.diffCache.keys().next().value;
      if (oldest === undefined) break;
      this.diffCache.delete(oldest);
    }
  }
  private clearDiffCache(): void { this.diffCache.clear(); }
  private invalidateWorktreeCache(): void {
    for (const key of [...this.diffCache.keys()]) {
      if (key.startsWith('worktree|')) this.diffCache.delete(key);
    }
  }
  private invalidateWorktreePreviewCache(): void {
    for (const key of [...this.previewCache.keys()]) {
      if (key.startsWith('worktree|')) this.previewCache.delete(key);
    }
  }

  // Commit-history view: when the task has an auto-commit recorded, the
  // pane switches from "live working tree" to "what this task changed".
  // That data survives future work in the repo and is what the user wants
  // to see when reviewing a finished task.
  readonly commitDetail = signal<TaskCommitDetail | null>(null);

  /**
   * User-triggered code-review artifacts for this task (newest first),
   * mirroring the Code Review tab's listing. Loaded lazily when the job is
   * set / gains a commit so the commit view can surface a compact rating
   * badge on the commit line (AGT-1995). Silent on error: the badge simply
   * stays hidden, it is never load-bearing for the diff/commit flow.
   */
  readonly codeReviews = signal<CodeReviewListEntry[]>([]);

  /**
   * The most recent code review whose reviewed commit matches the commit the
   * detail view is currently showing, or `null` when none matches (or when
   * the aggregated "all commits" view is active, which has no single commit
   * line). Drives the commit-row rating badge + its "jump to Code Review"
   * click.
   */
  readonly commitReview = computed<CodeReviewListEntry | null>(() => {
    const sha = this.commitDetail()?.commit?.sha ?? null;
    if (!sha) return null;
    return this.codeReviews().find((r) => reviewMatchesSha(r.commit, sha)) ?? null;
  });

  /**
   * Commit-provenance & landed-state (ASS-1724): the live, graph-derived view
   * of where this task's work lives (task/<id> -> develop -> main) plus
   * per-commit branch membership. Loaded lazily when the job is set / changes;
   * recomputed by the backend on every fetch so it never lies about how far
   * develop / main have advanced.
   */
  readonly provenance = signal<TaskProvenanceView | null>(null);

  /**
   * Whether the graph-derived provenance has resolved (success OR error) for the
   * currently-open job. Starts `false` on every job change and flips `true` the
   * first time `loadProvenance()` settles. Git-dependent UI (the detail-header
   * "Merge into Develop" / "Accept" acceptance primary) reads this to stay
   * disabled + show a loading state until the branch/merge truth is known, so a
   * still-loading `provenance() === null` no longer renders as an actionable
   * "not yet merged" button that later flips to "already merged" (AGT-2006).
   * A same-job refresh that re-pulls provenance leaves this `true` so the
   * acceptance primary does not flicker back into a skeleton on every poll.
   */
  readonly provenanceLoaded = signal(false);

  /**
   * Ordered chain of commits attributed to this task (oldest -&gt; newest).
   * Mirrors <c>TaskInfo.commits</c>; surfaces an in-memory list so the
   * git-pane can render a multi-commit strip and let the user pick which
   * commit's detail to display.
   */
  readonly commitChain = signal<TaskCommitInfo[]>([]);
  /** Commits still expected to land, excluding replaced delivery rounds. */
  readonly activeCommitChain = computed<TaskCommitInfo[]>(() =>
    this.commitChain().filter((commit) => !isSuperseded(commit)),
  );
  /**
   * SHA of the commit the detail view is filtered to. `null` is the
   * default for multi-commit tasks and means "all commits aggregated":
   * the file list + per-file diffs are combined across every commit
   * attributed to the task. A non-null SHA filters the view down to that
   * single commit. Single-commit tasks pin this to their one SHA (no
   * filter is offered).
   */
  readonly selectedCommitSha = signal<string | null>(null);

  /**
   * File list aggregated across every commit attributed to this task.
   * Populated only while {@link isAggregate} is active; the single-commit
   * file list lives on {@link commitDetail}.
   */
  readonly aggregateFiles = signal<GitFileChange[]>([]);

  /** Commit-history view is active whenever the task carries any commit. */
  readonly viewMode = computed<'commit' | 'worktree'>(() =>
    this.commitChain().length > 0 ? 'commit' : 'worktree',
  );

  /**
   * True when the detail view shows the combined diff across all task
   * commits. Only reachable for multi-commit tasks; a lone commit always
   * renders as itself so the redundant "all = the one commit" filter is
   * never shown.
   */
  readonly isAggregate = computed<boolean>(
    () => this.activeCommitChain().length > 1 && this.selectedCommitSha() === null,
  );

  /** Files backing the commit-mode tree: aggregated set, or the single commit's. */
  readonly commitFiles = computed<GitFileChange[]>(() =>
    this.isAggregate() ? this.aggregateFiles() : this.commitDetail()?.files ?? [],
  );

  private currentJob: TaskInfo | null = null;
  private refreshTimer: VisibleIntervalHandle | null = null;

  /** Start polling git status every `intervalMs` ms. No-op if already running. */
  startAutoRefresh(intervalMs = 5000): void {
    if (this.refreshTimer) return;
    this.refreshTimer = setVisibleInterval(() => {
      // In commit mode the displayed snapshot is historical — polling the
      // working tree would just churn for nothing.
      if (this.viewMode() === 'commit') return;
      if (!this.committing() && !this.generatingMsg()) {
        this.refresh();
      }
    }, intervalMs);
  }

  /** Stop the auto-refresh polling loop. */
  stopAutoRefresh(): void {
    if (this.refreshTimer) {
      clearVisibleInterval(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  ngOnDestroy(): void {
    this.stopAutoRefresh();
  }

  /**
   * Tell the service which job is currently displayed. Resets the pane
   * state when the job actually changes; same-job calls are no-ops so
   * we don't blow away in-flight selections.
   *
   * The task detail calls this from an effect. The pane state it reads is
   * untracked: a same-job refresh of a commit-less card writes a fresh empty
   * chain, which would otherwise re-run that effect forever (AGT-3096).
   */
  setJob(info: TaskInfo | null | undefined): void {
    untracked(() => this.applyJob(info));
  }

  private applyJob(info: TaskInfo | null | undefined): void {
    const sameJob =
      this.currentJob &&
      info &&
      this.currentJob.id === info.id &&
      this.currentJob.watchPath === info.watchPath;
    if (sameJob) {
      const oldChainSignature = commitChainSignature(this.commitChain());
      this.currentJob = info!;
      const chain = info!.commits ?? (info!.commit ? [info!.commit] : []);
      this.commitChain.set(chain);
      // The auto-commit lands on the progress→review transition, so a
      // refresh of the same job can flip from "no commit" to "has commit".
      // Load the snapshot lazily when that happens. Also reload when a
      // new commit lands on the chain (continue-mode follow-up, recovery
      // commit, operator-driven steer).
      const chainChanged = oldChainSignature !== commitChainSignature(chain);
      if (chainChanged) {
        this.applyCommitDefault(chain);
        // A new commit (or a just-landed merge) can move the landed-state, so
        // re-pull the graph-derived provenance when the chain grows.
        this.loadProvenance();
        // A follow-up commit may carry a fresh code-review verdict; refresh
        // the listing so the commit-row badge tracks the new commit.
        this.loadCodeReviews();
      }
      return;
    }
    this.currentJob = info ?? null;
    this.status.set(null);
    this.commitDetail.set(null);
    this.aggregateFiles.set([]);
    this.selectedDiffPath.set(null);
    this.diffText.set('');
    this.commitMessage.set('');
    this.loading.set(false);
    this.committing.set(false);
    this.generatingMsg.set(false);
    this.provenance.set(null);
    this.provenanceLoaded.set(false);
    this.codeReviews.set([]);
    this.clearDiffCache();
    this.previewCache.clear();
    this.resetPreview();
    const chain = info?.commits ?? (info?.commit ? [info.commit] : []);
    this.commitChain.set(chain);
    this.applyCommitDefault(chain);
    this.loadProvenance();
    this.loadCodeReviews();
  }

  /**
   * Load the code-review listing for the current job (newest first). Silent
   * on error - the commit-row rating badge simply stays hidden. No-op in
   * worktree-only tasks with no commit, but harmless to call regardless.
   */
  loadCodeReviews(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.listCodeReviews(info.id, info.watchPath).subscribe({
      // Guard the shape explicitly: the listing contract is `{ entries: [...] }`,
      // but a malformed/unexpected body (e.g. a bare array) would make
      // `resp.entries` resolve to `Array.prototype.entries` - a truthy function
      // that slips past `?? []` and then throws `.find is not a function` inside
      // the `commitReview` computed, aborting the git-pane's change-detection
      // pass and leaving the commit header half-rendered. Only trust an actual
      // array here so the rating badge stays a strictly best-effort overlay.
      next: (resp) => this.codeReviews.set(Array.isArray(resp?.entries) ? resp.entries : []),
      error: () => this.codeReviews.set([]),
    });
  }

  /**
   * Load the graph-derived commit-provenance view for the current job. Silent
   * on error (the ladder + membership strip simply stays hidden): provenance is
   * a read-only "where does this live" overlay, never load-bearing for the
   * pane's primary diff/commit flow.
   */
  loadProvenance(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.getTaskProvenance(info.id, info.watchPath).subscribe({
      // Flip the resolved flag on both settle paths: a failed load still means
      // "we are no longer waiting". Acceptance still reads target membership
      // only from the task's computed integration field.
      next: (view) => { this.provenance.set(view); this.provenanceLoaded.set(true); },
      error: () => { this.provenance.set(null); this.provenanceLoaded.set(true); },
    });
  }

  /**
   * Pick the default commit-detail view for a freshly-set chain:
   *   - empty chain  -> no commit view (worktree mode).
   *   - one commit   -> that commit (no aggregate filter is offered).
   *   - many commits -> the aggregated "all commits" diff.
   */
  private applyCommitDefault(chain: TaskCommitInfo[]): void {
    if (chain.length === 0) {
      this.selectedCommitSha.set(null);
      this.aggregateFiles.set([]);
      return;
    }
    const active = chain.filter((commit) => !isSuperseded(commit));
    if (active.length > 1) {
      this.selectedCommitSha.set(null);
      this.loadAggregate();
    } else {
      const selected = active[0] ?? chain[chain.length - 1];
      this.selectedCommitSha.set(selected.sha);
      this.loadCommitDetail();
    }
  }

  /**
   * Switch the detail view back to the aggregated "all commits" diff.
   * No-op when already aggregated. Drops the single-commit detail so the
   * aggregate header + combined file list take over.
   */
  selectAllCommits(): void {
    if (this.selectedCommitSha() === null) return;
    this.selectedCommitSha.set(null);
    this.selectedDiffPath.set(null);
    this.diffText.set('');
    this.commitDetail.set(null);
    this.loadAggregate();
  }

  /**
   * Load the file list aggregated across every commit attributed to this
   * task. Default-selects the first changed file so the combined diff is
   * visible without a click.
   */
  private loadAggregate(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.getJobCommitFilesAggregate(info.id, info.watchPath).subscribe({
      next: (res) => {
        const files: GitFileChange[] = res?.files ?? [];
        this.aggregateFiles.set(files);
        this.selectedDiffPath.set(null);
        this.diffText.set('');
        const first = files[0]?.path ?? null;
        if (first) this.selectDiffPath(first);
      },
      error: () => this.aggregateFiles.set([]),
    });
  }

  /**
   * Switch the commit detail view to a specific commit on the task's
   * chain. Validates the SHA against the chain to keep this method's
   * blast radius bounded - it cannot be coaxed into loading arbitrary
   * repository commits even before the backend's IsKnownJobCommit
   * gate refuses unrelated SHAs.
   */
  selectChainCommit(sha: string): void {
    const info = this.currentJob;
    if (!info) return;
    const entry = this.commitChain().find((c) => c.sha === sha);
    if (!entry) return;
    if (this.selectedCommitSha() === sha) return;
    this.selectedCommitSha.set(sha);
    this.selectedDiffPath.set(null);
    this.diffText.set('');
    // Compose a TaskCommitDetail header from the chain entry directly,
    // then load the file list from the per-sha endpoint. We don't have
    // a single "commit by sha" endpoint that returns both, so we shape
    // the same object here for the template's sake.
    this.commitDetail.set({ commit: entry, files: [] });
    this.jobService.getJobCommitFilesBySha(info.id, sha, info.watchPath).subscribe({
      next: (res) => {
        const files: GitFileChange[] = res?.files ?? [];
        this.commitDetail.set({ commit: entry, files });
        const first = files[0]?.path ?? null;
        if (first) this.selectDiffPath(first);
      },
      error: () => this.commitDetail.set({ commit: entry, files: [] }),
    });
  }

  /**
   * Load the recorded-commit snapshot for the current job. Tasks that have
   * been auto-committed on progress→review carry a TaskCommitInfo on
   * job.json — the backend re-derives the file list from `git show` so the
   * pane stays accurate even after history rewrites.
   */
  loadCommitDetail(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.getJobCommit(info.id, info.watchPath).subscribe({
      next: (detail) => {
        this.commitDetail.set(detail);
        // Default-select the first changed file so the diff is visible at a
        // glance — matches the user's intent of "show me the changes".
        const first = detail?.files?.[0]?.path ?? null;
        if (first) this.selectDiffPath(first);
      },
      error: () => this.commitDetail.set(null),
    });
  }

  refresh(): void {
    const info = this.currentJob;
    if (!info) return;
    this.loading.set(true);
    // Worktree diff slice can shift between refreshes (file edited again,
    // staged, etc.); drop any cached worktree entries so the next click
    // picks up a fresh diff. Commit-mode entries are immutable per sha,
    // so we keep those.
    this.invalidateWorktreeCache();
    this.invalidateWorktreePreviewCache();
    this.jobService.getGitStatus(info.id, info.watchPath).subscribe({
      next: (status) => {
        this.status.set(status);
        this.loading.set(false);
        // If a previously selected file is no longer in the change set,
        // clear the diff so we don't keep stale text on screen.
        const selected = this.selectedDiffPath();
        if (selected && !status.files.some((f) => f.path === selected)) {
          this.selectedDiffPath.set(null);
          this.diffText.set('');
          this.resetPreview();
        }
      },
      error: (err) => {
        this.loading.set(false);
        this.errorDialog.show(err, { title: 'Git status failed', source: `Task ${info.id}` });
      },
    });
  }

  selectDiffPath(path: string): void {
    // Preview state is per-selected-file; drop it whenever the selection moves
    // so a stale .md/.html render never lingers under a different file.
    this.resetPreview();
    if (this.selectedDiffPath() === path) {
      this.selectedDiffPath.set(null);
      this.diffText.set('');
      return;
    }
    const info = this.currentJob;
    if (!info) return;
    this.selectedDiffPath.set(path);
    // Cache lookup: clicking back-and-forth between files in the tree is
    // a common pattern; without the cache each toggle pays the network
    // round-trip and the diff2html re-render. Hit -> set synchronously.
    const cacheKey = this.cacheKey(path);
    const cached = this.cacheGet(cacheKey);
    if (cached !== undefined) {
      this.diffText.set(cached);
      return;
    }
    this.diffText.set('');
    // Diff fetches are async, but the user can click another file before a
    // slow round-trip resolves. Without a guard the late response writes its
    // text into `diffText` even though `selectedDiffPath` has since moved on,
    // so the pane shows file A's diff under file B's highlight + path label.
    // Pin the request to the path it was issued for and drop any result that
    // is no longer the selected file. We still populate the cache so a later
    // click on that path is served instantly.
    const stillSelected = () => this.selectedDiffPath() === path;
    // In commit mode the diff comes from `git show <sha> -- <path>` so we
    // see the historical change, not whatever the working tree looks like
    // right now. When the selected commit is one of the older entries on
    // the chain (not the newest singular commit), route to the per-sha
    // diff endpoint instead so the displayed diff matches the picked
    // commit, not whichever one happens to be on `TaskInfo.commit`.
    if (this.viewMode() === 'commit') {
      const selectedSha = this.selectedCommitSha();
      const setDiff = (res: unknown) => {
        let text = '';
        if (typeof res === 'string') text = res;
        else if (res && typeof res === 'object' && 'diff' in (res as Record<string, unknown>)) {
          const d = (res as { diff?: string }).diff;
          text = typeof d === 'string' ? d : '';
        }
        if (text) this.cachePut(cacheKey, text);
        if (!stillSelected()) return;
        this.diffText.set(text);
      };
      const handlers = {
        next: setDiff,
        error: () => { if (stillSelected()) this.diffText.set('(failed to load diff)'); },
      };
      if (selectedSha === null) {
        // Aggregated default: combined diff across all task commits.
        this.jobService
          .getJobCommitDiffAggregate(info.id, path, info.watchPath)
          .subscribe(handlers);
      } else {
        this.jobService
          .getJobCommitDiffBySha(info.id, selectedSha, path, info.watchPath)
          .subscribe(handlers);
      }
      return;
    }
    this.jobService.getGitDiff(info.id, path, info.watchPath).subscribe({
      next: (text: unknown) => {
        const t = typeof text === 'string' ? text : '';
        if (t) this.cachePut(cacheKey, t);
        if (!stillSelected()) return;
        this.diffText.set(t);
      },
      error: () => { if (stillSelected()) this.diffText.set('(failed to load diff)'); },
    });
  }

  /**
   * Fetch the formatted-preview source for a path (md/html rendering). Serves
   * from an LRU cache when possible so toggling Diff <-> Preview is instant;
   * otherwise pulls the file text from the ref that backs the current view
   * (working tree, the selected commit, or the newest task commit for the
   * aggregated diff). Late responses are dropped when the selection has moved
   * on, mirroring {@link selectDiffPath}'s stale-guard.
   */
  loadPreview(path: string): void {
    const info = this.currentJob;
    if (!info || !path) return;

    const key = this.previewKey(path);
    const cached = this.previewCacheGet(key);
    if (cached) {
      this.previewContent.set(cached.content);
      this.previewIsBinary.set(cached.isBinary);
      this.previewError.set(null);
      this.previewLoading.set(false);
      return;
    }

    this.previewContent.set(null);
    this.previewIsBinary.set(false);
    this.previewError.set(null);
    this.previewLoading.set(true);

    const stillSelected = () => this.selectedDiffPath() === path;
    const apply = (res: { content?: string; isBinary?: boolean } | null) => {
      const content = typeof res?.content === 'string' ? res.content : '';
      const isBinary = res?.isBinary === true;
      this.previewCachePut(key, { content, isBinary });
      if (!stillSelected()) return;
      this.previewLoading.set(false);
      this.previewIsBinary.set(isBinary);
      this.previewContent.set(isBinary ? '' : content);
    };
    const fail = () => {
      if (!stillSelected()) return;
      this.previewLoading.set(false);
      this.previewError.set('Failed to load preview.');
    };

    if (this.viewMode() === 'commit') {
      // Aggregated view has no single commit; preview the file at the newest
      // task commit so the "final" version is shown. A single-commit view uses
      // that commit's blob so the preview matches its diff.
      const sha = this.selectedCommitSha() ?? this.newestCommitSha();
      if (!sha) { this.previewLoading.set(false); this.previewError.set('No commit to preview.'); return; }
      this.jobService.getJobCommitFileBySha(info.id, sha, path, info.watchPath).subscribe({ next: apply, error: fail });
      return;
    }
    this.jobService.getGitFileContent(info.id, path, info.watchPath).subscribe({ next: apply, error: fail });
  }

  /** Newest SHA on the task's commit chain (the chain is oldest -> newest). */
  private newestCommitSha(): string | null {
    const active = this.activeCommitChain();
    const chain = active.length > 0 ? active : this.commitChain();
    return chain.length ? chain[chain.length - 1].sha : null;
  }

  private previewKey(path: string): string {
    if (this.viewMode() === 'commit') {
      const sha = this.selectedCommitSha() ?? this.newestCommitSha() ?? '';
      return `commit|${sha}|${path}`;
    }
    return `worktree|${path}`;
  }
  private previewCacheGet(key: string): { content: string; isBinary: boolean } | undefined {
    const v = this.previewCache.get(key);
    if (v !== undefined) { this.previewCache.delete(key); this.previewCache.set(key, v); }
    return v;
  }
  private previewCachePut(key: string, value: { content: string; isBinary: boolean }): void {
    if (this.previewCache.has(key)) this.previewCache.delete(key);
    this.previewCache.set(key, value);
    while (this.previewCache.size > this.PREVIEW_CACHE_LIMIT) {
      const oldest = this.previewCache.keys().next().value;
      if (oldest === undefined) break;
      this.previewCache.delete(oldest);
    }
  }
  private resetPreview(): void {
    this.previewContent.set(null);
    this.previewIsBinary.set(false);
    this.previewError.set(null);
    this.previewLoading.set(false);
  }

  generateCommitMessage(): void {
    const info = this.currentJob;
    if (!info) return;
    this.generatingMsg.set(true);
    this.jobService.generateCommitMessage(info.id, info.watchPath).subscribe({
      next: (res) => {
        this.generatingMsg.set(false);
        if (res?.message) this.commitMessage.set(res.message);
      },
      error: (err) => {
        this.generatingMsg.set(false);
        this.errorDialog.show(err, {
          title: 'Generate commit message failed',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  commit(): void {
    const info = this.currentJob;
    const msg = this.commitMessage().trim();
    if (!info || !msg) return;
    this.committing.set(true);
    this.jobService.commitJob(info.id, msg, info.watchPath).subscribe({
      next: () => {
        this.committing.set(false);
        this.commitMessage.set('');
        this.refresh();
      },
      error: (err) => {
        this.committing.set(false);
        this.errorDialog.show(err, { title: 'Commit failed', source: `Task ${info.id}` });
      },
    });
  }

  openInVsCode(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.openInVsCode(info.id, info.watchPath).subscribe({
      error: (err) =>
        this.errorDialog.show(err, { title: 'Open in VS Code failed', source: `Task ${info.id}` }),
    });
  }
}

function commitChainSignature(chain: TaskCommitInfo[]): string {
  return chain
    .map((commit) => `${commit.sha}:${commit.supersededBySha?.trim() ?? ''}:${commit.supersededByAttempt?.trim() ?? ''}`)
    .join('|');
}

function isSuperseded(commit: TaskCommitInfo): boolean {
  return !!commit.supersededBySha?.trim() || !!commit.supersededByAttempt?.trim();
}

/**
 * Match a code-review entry's reviewed-commit field against a commit SHA.
 * The review may record either a full or an abbreviated SHA (it stores
 * whatever HEAD resolved to at review time), so we accept a prefix match in
 * either direction rather than requiring equal-length strings.
 */
function reviewMatchesSha(reviewCommit: string | null | undefined, sha: string): boolean {
  if (!reviewCommit || !sha) return false;
  return sha === reviewCommit || sha.startsWith(reviewCommit) || reviewCommit.startsWith(sha);
}
