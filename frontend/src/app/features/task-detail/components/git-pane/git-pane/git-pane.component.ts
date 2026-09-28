import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { GitPaneService } from '../../../services/git-pane.service';
import { LayoutPanesService } from '../../../services/layout-panes.service';
import { GitFileTreeComponent } from '../git-file-tree/git-file-tree.component';
import type { TaskLandedLadder } from '../../../../git';
import type { CodeReviewListEntry } from '../../../../../services/task.service';
import {
  codeReviewVerdictGlyph,
  codeReviewVerdictLabel,
  codeReviewVerdictTone,
  type CodeReviewVerdictTone,
} from '../../code-review-verdict.util';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../../../../components/tooltip/app-tooltip.directive';
import { isLargeDiff, describeDiffSize } from '../../../../../utils/large-diff-gate';
import { coalesceDiffByFile } from '../../../../../utils/coalesce-diff';
import { currentDiff2Html, hasDiff2HtmlLoaded, loadDiff2Html } from '../../../../../utils/diff2html-lazy';
import { ResizableSplitterDirective } from '../../../../../components/resizable-splitter/resizable-splitter.directive';
import { TaskCommitRoundsComponent } from '../task-commit-rounds/task-commit-rounds.component';
// Cycle 7f: diff2html (~120 KB minified, includes its own theme CSS) is
// loaded lazily the first time a non-empty diff arrives. The pre-Cycle-7f
// import was static, which dragged the whole library into the initial
// chunk even though most users never open the git pane on first paint.
// The lazy module + dark-color-scheme constant are cached after first
// load so the second diff render is synchronous.
// The loader itself lives in utils so every diff surface shares the same
// cached module and the same large-diff gate can suppress that import.

/**
 * Renders the Git pane of the job-detail view: working-tree status,
 * per-file diff, and commit form. State + API calls live in
 * GitPaneService (provided locally on TaskDetailComponent); this
 * component is purely presentational.
 *
 * The selected file's unified-diff text is rendered through
 * `diff2html` so users see syntax-aware add/remove highlighting and
 * (when maximized) a side-by-side view. The diff section has its own
 * maximize toggle independent of the surrounding pane: in-pane it uses
 * `line-by-line` to fit the narrow column, and switches to
 * `side-by-side` when the diff is fullscreened.
 */
@Component({
  selector: 'app-git-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, NgTemplateOutlet, GitFileTreeComponent, TaskCommitRoundsComponent, TooltipDirective, AppTooltipDirective, MarkdownViewComponent, ResizableSplitterDirective],
  templateUrl: './git-pane.component.html',
  styleUrls: ['./git-pane.component.scss']
})
export class GitPaneComponent {
  /** Whether this pane is currently the maximized one. */
  readonly maximized = input(false);
  /** Flex weight to apply when not maximized. */
  readonly weight = input<number>(1);
  /** Whether the job's CLI is currently running — disables commit/generate. */
  readonly isRunning = input(false);
  /**
   * Whether this job is the runner's currently-active job for its
   * project. Worktree-isolation rule: the working tree is shared
   * across the whole repository, so live `git status` output belongs to
   * whichever task the agent is currently editing - i.e. the active
   * one. On non-active tasks this pane suppresses the working-tree
   * view entirely and renders a placeholder; only `git show <sha>`-
   * derived diffs from this task's own commits are still shown.
   */
  readonly isActiveJob = input<boolean>(false);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();

  readonly git = inject(GitPaneService);
  private readonly layout = inject(LayoutPanesService);
  private readonly sanitizer = inject(DomSanitizer);

  /** Diff section fullscreen toggle, scoped to this component. */
  readonly diffMaximized = signal(false);

  /**
   * Collapse toggle for the commit-message banner above the tree+diff
   * split. Persisted in localStorage so the operator's preference
   * survives a reload. One toggle drives both the in-pane and
   * fullscreened layouts so the mental model stays simple.
   */
  readonly commitHeaderCollapsed = signal<boolean>(readCommitHeaderCollapsed());

  toggleCommitHeaderCollapsed(): void {
    const next = !this.commitHeaderCollapsed();
    this.commitHeaderCollapsed.set(next);
    writeCommitHeaderCollapsed(next);
  }

  /**
   * Master collapse for the whole commit-meta head (landed ladder +
   * "N task commits" group + per-commit banner) that sits above the
   * tree/diff split. Collapsing it hands all the vertical space to the
   * tree + diff so the review surface reads compact; persisted so the
   * operator's preference survives a reload. Default expanded so the
   * ladder / commit context stays visible for first-time viewers.
   */
  readonly headCollapsed = signal<boolean>(readHeadCollapsed());

  toggleHeadCollapsed(): void {
    const next = !this.headCollapsed();
    this.headCollapsed.set(next);
    writeHeadCollapsed(next);
  }

  /** Compact label for the collapsed head strip. */
  readonly headTitle = computed<string>(() => {
    const n = currentCommitCount(this.git.commitChain());
    return n > 1 ? `${n} task commits` : 'Commit details';
  });

  /**
   * Diff render layout. Side-by-side shows the before/after columns; the
   * unified (inline) mode collapses to a single "just the change" column
   * per the operator's ask. Persisted; default side-by-side. This is now
   * the single source of truth for the diff2html `outputFormat` - the
   * previous behaviour (implicitly side-by-side only while maximized,
   * line-by-line otherwise) is replaced by this explicit, remembered
   * toggle so maximizing no longer silently changes the layout.
   */
  readonly diffViewMode = signal<DiffViewMode>(readDiffViewMode());

  toggleDiffViewMode(): void {
    const next: DiffViewMode = this.diffViewMode() === 'side-by-side' ? 'line-by-line' : 'side-by-side';
    this.diffViewMode.set(next);
    writeDiffViewMode(next);
  }

  // --- Tree | diff splitter (draggable, persisted) -----------------------
  // In the split (pane-maximized) layout the file-change tree sits left of
  // the diff. The divider between them is draggable; its width is stored in
  // px and pushed to the tree column through the `--git-tree-width` custom
  // property. The shared resizable splitter directive owns pointer and
  // keyboard resizing, persistence, and the readable floors for both sides.
  /**
   * Title rendered in the pane header. Surfaces the multi-commit case
   * ("3 task commits") so the user sees at a glance that the chain has
   * more than one entry.
   */
  readonly paneTitle = computed<string>(() => {
    if (this.git.viewMode() === 'commit') {
      const n = currentCommitCount(this.git.commitChain());
      if (n > 1) return `⏺ ${n} task commits`;
      return '⏺ Task commit';
    }
    return '⎇ Git view';
  });

  /** Tracks whether the diff2html module has finished its dynamic import. */
  private readonly diff2htmlReady = signal(hasDiff2HtmlLoaded());

  // Trigger the lazy import the first time we're asked to render a diff.
  // Until the module is in memory, diffHtml() returns null and the
  // template shows a small placeholder; the moment the import resolves
  // the signal flips and the computed re-runs synchronously.
  private readonly _ensureDiff2HtmlLoaded = effect(() => {
    const text = this.git.diffText();
    if (!text) return;
    if (this.diffGated()) return;
    if (this.diff2htmlReady()) return;
    loadDiff2Html().then(() => this.diff2htmlReady.set(true));
  });

  /**
   * Large-file gate (central threshold in utils/large-diff-gate). Big
   * diffs are not auto-rendered - the full diff2html render of a huge
   * block is what makes the pane feel slow - so a compact placeholder is
   * shown until the operator clicks "Show diff". Reveal is remembered
   * per-path for the session, plus a "show all" escape hatch.
   */
  private readonly revealedPaths = signal<Set<string>>(new Set<string>());
  readonly revealAllLargeDiffs = signal(false);

  readonly diffIsLarge = computed<boolean>(() => isLargeDiff(this.git.diffText()));
  readonly diffSizeLabel = computed<string>(() => describeDiffSize(this.git.diffText()));

  /** Status char (A/M/D/…) of the selected file, for the gated placeholder. */
  readonly selectedFileStatus = computed<string>(() => {
    const path = this.git.selectedDiffPath();
    if (!path) return '';
    const fromCommit = this.git.commitFiles().find((f) => f.path === path);
    if (fromCommit) return fromCommit.status;
    const fromStatus = this.git.status()?.files.find((f) => f.path === path);
    return fromStatus?.status ?? '';
  });

  /** True when the current diff is large and the operator hasn't revealed it. */
  readonly diffGated = computed<boolean>(() => {
    if (!this.diffIsLarge()) return false;
    if (this.revealAllLargeDiffs()) return false;
    const path = this.git.selectedDiffPath();
    return !(path && this.revealedPaths().has(path));
  });

  revealCurrentDiff(): void {
    const path = this.git.selectedDiffPath();
    if (!path) return;
    const next = new Set(this.revealedPaths());
    next.add(path);
    this.revealedPaths.set(next);
  }

  revealAll(): void {
    this.revealAllLargeDiffs.set(true);
  }

  readonly diffHtml = computed<SafeHtml | null>(() => {
    const text = this.git.diffText();
    if (!text) return null;
    if (this.diffGated()) return null;
    const diff2html = currentDiff2Html();
    if (!this.diff2htmlReady() || !diff2html) return null;
    const sideBySide = this.diffViewMode() === 'side-by-side';
    // Group multiple same-file sections (the aggregate diff concatenates one
    // `diff --git` block per attributed commit) under a single file header so
    // a file changed across several commits no longer renders as repeated
    // "README" blocks - the AGT-2008 "Datei-Gruppierung" fix.
    const rendered = diff2html.html(coalesceDiffByFile(text), {
      drawFileList: false,
      outputFormat: sideBySide ? 'side-by-side' : 'line-by-line',
      matching: 'lines',
      colorScheme: diff2html.darkScheme,
    });
    return this.sanitizer.bypassSecurityTrustHtml(rendered);
  });

  toggleDiffMaximize(): void {
    this.diffMaximized.update(v => !v);
  }

  // --- md/html preview (AGT-2008) ---------------------------------------
  // Changed .md/.html files can be shown as a rendered preview instead of a
  // raw diff (UI-feedback 2026-07-09, observed on two README.md). The toggle
  // is per selected file and defaults to Diff; markdown renders through the
  // shared <cac-markdown> surface, html through a script-enabled but
  // opaque-origin sandbox. Interactive artifacts can run, but omitting
  // allow-same-origin keeps Studio cookies, storage, DOM, and APIs isolated.

  /** Whether the rendered preview (vs. the diff) is shown for the current file. */
  readonly previewActive = signal(false);

  /** Preview kind for the selected file, or null when it is not previewable. */
  readonly previewKind = computed<'markdown' | 'html' | null>(() =>
    previewKindOf(this.git.selectedDiffPath()),
  );

  readonly selectedIsPreviewable = computed<boolean>(() => this.previewKind() !== null);

  /**
   * Reset the Diff/Preview toggle back to Diff whenever the selected file
   * changes or stops being previewable, so a leftover "Preview" state never
   * applies to a file that has no preview.
   */
  private readonly _resetPreviewOnSelectionChange = effect(() => {
    if (!this.selectedIsPreviewable() && this.previewActive()) {
      this.previewActive.set(false);
    }
  });

  togglePreview(): void {
    const next = !this.previewActive();
    this.previewActive.set(next);
    const path = this.git.selectedDiffPath();
    if (next && path) this.git.loadPreview(path);
  }

  /**
   * Keep the preview in sync when the operator switches to another previewable
   * file while Preview stays active: `selectDiffPath` clears the old content,
   * so kick a fresh load for the new path. Scheduled off the effect's
   * synchronous pass (microtask) so it never writes signals mid-effect; the
   * guards stop it re-firing once a load is in flight or has resolved.
   */
  private readonly _loadPreviewWhenActive = effect(() => {
    const path = this.git.selectedDiffPath();
    const active = this.previewActive();
    const previewable = this.selectedIsPreviewable();
    const empty = this.git.previewContent() === null
      && !this.git.previewIsBinary()
      && !this.git.previewLoading()
      && !this.git.previewError();
    if (!path || !active || !previewable || !empty) return;
    Promise.resolve().then(() => this.git.loadPreview(path));
  });

  /** `allow-scripts` enables interaction; omitted same-origin isolates Studio state and APIs. */
  readonly previewHtmlDoc = computed<SafeHtml | null>(() => {
    if (this.previewKind() !== 'html') return null;
    const content = this.git.previewContent();
    if (content == null) return null;
    return this.sanitizer.bypassSecurityTrustHtml(content);
  });

  selectCommitFromHistory(sha: string | null): void {
    if (sha === null) this.git.selectAllCommits();
    else this.git.selectChainCommit(sha);
  }

  selectedCommitSummary(): string {
    if (this.git.isAggregate()) {
      const files = this.git.commitFiles().length;
      const commits = currentCommitCount(this.git.commitChain());
      return `All ${commits} commits · ${files} ${files === 1 ? 'file' : 'files'}`;
    }
    const sha = this.git.selectedCommitSha();
    const entry = this.git.commitChain().find(c => c.sha === sha);
    if (!entry) return `${currentCommitCount(this.git.commitChain())} current task commits`;
    return `${entry.shortSha} · ${entry.message.split('\n')[0]}`;
  }

  // --- Commit-provenance / landed-ladder (ASS-1724) ---------------------
  // The landed ladder (task/<id> -> develop -> main) is derived live off the
  // git graph by the backend and read through git.provenance(). It is the
  // single place the task's develop-merged / main-pending state is shown
  // (UI-feedback 2026-07-09: the redundant "Merged to develop" pill and the
  // per-commit "on develop" membership chips were removed). Each rung carries
  // its own reached/pending tooltip inline in the template.

  /** Short-SHA display; em-dash placeholder when a rung has no resolved HEAD. */
  short(sha: string | null | undefined): string {
    if (!sha) return '—';
    return sha.length > 7 ? sha.slice(0, 7) : sha;
  }

  /**
   * Reached/pending tooltip for the develop-integration ladder rung. Kept as
   * a component method (rather than an inline template ternary) so the rung's
   * conditional stays within the angular-eslint template conditional-complexity
   * budget; the string is identical to the former inline expression.
   */
  integrationRungTooltip(ladder: TaskLandedLadder): string {
    const head = this.short(ladder.integrationHead);
    return ladder.mergedToIntegration
      ? `Merged into ${ladder.integrationBranch} (HEAD now ${head})`
      : `Not yet merged into ${ladder.integrationBranch} (HEAD now ${head})`;
  }

  /** Reached/pending tooltip for the release ladder rung. See {@link integrationRungTooltip}. */
  releaseRungTooltip(ladder: TaskLandedLadder): string {
    const head = this.short(ladder.releaseHead);
    return ladder.releasedToRelease
      ? `Released into ${ladder.releaseBranch} (HEAD now ${head})`
      : `Not yet released into ${ladder.releaseBranch} (HEAD now ${head})`;
  }

  // --- Commit-row code-review rating badge (AGT-1995) --------------------
  // A compact indicator of the code-review verdict for the commit currently
  // shown on the commit line. Tone/label/glyph come from the shared verdict
  // util so this stays in lockstep with the Code Review tab; the review data
  // itself lives on GitPaneService.commitReview().

  reviewTone(verdict: string | null | undefined): CodeReviewVerdictTone {
    return codeReviewVerdictTone(verdict);
  }

  reviewLabel(verdict: string | null | undefined): string {
    return codeReviewVerdictLabel(verdict);
  }

  reviewGlyph(verdict: string | null | undefined): string {
    return codeReviewVerdictGlyph(verdict);
  }

  /** Tooltip for the rating badge: verdict + one-line summary + affordance. */
  reviewTooltip(review: CodeReviewListEntry): string {
    const label = codeReviewVerdictLabel(review.verdict);
    const summary = (review.summary ?? '').trim();
    const head = summary ? `Code review: ${label} · ${summary}` : `Code review: ${label}`;
    return `${head}. Click to open the Code Review tab.`;
  }

  /**
   * Reveal the prompt pane (if hidden) and focus its Code Review tab. Routed
   * through the shared layout service rather than an @Output so the git pane
   * does not need the task-detail shell to mediate a same-feature navigation.
   */
  openCodeReview(): void {
    this.layout.openPromptTab('code-review');
  }
}

export type DiffViewMode = 'side-by-side' | 'line-by-line';

/**
 * Classify a changed-file path for the rendered preview: markdown for
 * `.md`/`.markdown`, html for `.html`/`.htm`, null for everything else.
 * Extension match is case-insensitive; the query/hash-free basename is used
 * so a path is never mis-classified by a trailing fragment.
 */
export function previewKindOf(path: string | null | undefined): 'markdown' | 'html' | null {
  if (!path) return null;
  const clean = path.split(/[?#]/)[0];
  const dot = clean.lastIndexOf('.');
  if (dot < 0) return null;
  const ext = clean.slice(dot + 1).toLowerCase();
  if (ext === 'md' || ext === 'markdown') return 'markdown';
  if (ext === 'html' || ext === 'htm') return 'html';
  return null;
}

function currentCommitCount(
  commits: readonly { supersededBySha?: string | null; supersededByAttempt?: string | null }[],
): number {
  return commits.filter((commit) =>
    !commit.supersededBySha?.trim() && !commit.supersededByAttempt?.trim()).length;
}

const COMMIT_HEADER_COLLAPSED_KEY = 'taskboard.gitPane.commitHeaderCollapsed';
const HEAD_COLLAPSED_KEY = 'taskboard.gitPane.headCollapsed';
const DIFF_VIEW_MODE_KEY = 'taskboard.gitPane.diffViewMode';

function readCommitHeaderCollapsed(): boolean {
  try { return localStorage.getItem(COMMIT_HEADER_COLLAPSED_KEY) === '1'; }
  catch { return false; }
}

function writeCommitHeaderCollapsed(value: boolean): void {
  try { localStorage.setItem(COMMIT_HEADER_COLLAPSED_KEY, value ? '1' : '0'); }
  catch { /* ignore quota / privacy-mode errors */ }
}

function readHeadCollapsed(): boolean {
  try { return localStorage.getItem(HEAD_COLLAPSED_KEY) === '1'; }
  catch { return false; }
}

function writeHeadCollapsed(value: boolean): void {
  try { localStorage.setItem(HEAD_COLLAPSED_KEY, value ? '1' : '0'); }
  catch { /* ignore quota / privacy-mode errors */ }
}

function readDiffViewMode(): DiffViewMode {
  try {
    return localStorage.getItem(DIFF_VIEW_MODE_KEY) === 'line-by-line' ? 'line-by-line' : 'side-by-side';
  }
  catch { return 'side-by-side'; }
}

function writeDiffViewMode(value: DiffViewMode): void {
  try { localStorage.setItem(DIFF_VIEW_MODE_KEY, value); }
  catch { /* ignore quota / privacy-mode errors */ }
}
