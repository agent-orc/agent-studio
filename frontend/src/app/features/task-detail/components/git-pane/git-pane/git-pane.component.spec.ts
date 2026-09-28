import { computed, signal } from '@angular/core';
import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { GitPaneComponent, previewKindOf } from './git-pane.component';
import { GitPaneService } from '../../../services/git-pane.service';
import { LayoutPanesService } from '../../../services/layout-panes.service';
import type { GitFileChange, GitStatus, TaskCommitDetail, TaskCommitInfo, TaskProvenanceView } from '../../../../git';
import type { CodeReviewListEntry } from '../../../../../services/task.service';
import { LARGE_DIFF_LINE_THRESHOLD } from '../../../../../utils/large-diff-gate';
import { formatCompactDateTime, formatTime } from '../../../../../services/format.util';

const commits: TaskCommitInfo[] = [
  {
    sha: '1111111111111111111111111111111111111111',
    shortSha: '1111111',
    message: 'First commit',
    filesChanged: 1,
    files: ['src/one.ts'],
    at: '2026-06-08T10:00:00Z',
    attribution: 'automatic',
    confidence: 0.9,
  },
  {
    sha: '2222222222222222222222222222222222222222',
    shortSha: '2222222',
    message: 'Second commit',
    filesChanged: 1,
    files: ['src/two.ts'],
    at: '2026-06-09T10:00:00Z',
    attribution: 'automatic',
    confidence: 0.95,
  },
];

const files: GitFileChange[] = [
  {
    path: 'src/one.ts',
    status: 'M',
    added: 3,
    removed: 1,
  },
];

function worktreeStatus(isWorktree: boolean): GitStatus {
  return {
    isRepo: true,
    branch: isWorktree ? 'task/demo-task' : 'develop',
    filesChanged: 0,
    totalAdded: 0,
    totalRemoved: 0,
    files: [],
    error: null,
    isWorktree,
  };
}

function makeGitPaneMock(options?: {
  viewMode?: 'commit' | 'worktree';
  status?: GitStatus | null;
  codeReviews?: CodeReviewListEntry[];
  selectedDiffPath?: string | null;
  /** Content loadPreview() should publish for the selected file. */
  previewOnLoad?: { content?: string; isBinary?: boolean };
}) {
  const selectedCommitSha = signal<string | null>(null);
  const commitChain = signal<TaskCommitInfo[]>(commits);
  const commitFiles = signal<GitFileChange[]>(files);
  const commitDetail = signal<TaskCommitDetail | null>(null);
  const codeReviews = signal<CodeReviewListEntry[]>(options?.codeReviews ?? []);
  const previewContent = signal<string | null>(null);
  const previewIsBinary = signal(false);
  const previewLoading = signal(false);
  const previewError = signal<string | null>(null);

  return {
    previewContent,
    previewIsBinary,
    previewLoading,
    previewError,
    loadPreview: () => {
      const p = options?.previewOnLoad;
      previewContent.set(p?.content ?? null);
      previewIsBinary.set(p?.isBinary ?? false);
      previewError.set(null);
      previewLoading.set(false);
    },
    viewMode: signal<'commit' | 'worktree'>(options?.viewMode ?? 'commit'),
    commitChain,
    selectedCommitSha,
    selectedDiffPath: signal<string | null>(options?.selectedDiffPath ?? 'src/one.ts'),
    diffText: signal(''),
    loading: signal(false),
    status: signal<GitStatus | null>(options?.status ?? null),
    provenance: signal<TaskProvenanceView | null>(null),
    // Default to "resolved" so these render tests don't paint the AGT-2006
    // landed-status skeleton over their expectations; a dedicated case flips
    // it to false to assert the loading placeholder.
    provenanceLoaded: signal<boolean>(true),
    commitFiles,
    commitDetail,
    codeReviews,
    // Mirrors the real service computed: the newest review whose reviewed
    // commit matches the shown commit's SHA (prefix-tolerant), else null.
    commitReview: computed<CodeReviewListEntry | null>(() => {
      const sha = commitDetail()?.commit?.sha ?? null;
      if (!sha) return null;
      return (
        codeReviews().find(
          (r) => !!r.commit && (sha === r.commit || sha.startsWith(r.commit) || r.commit.startsWith(sha)),
        ) ?? null
      );
    }),
    isAggregate: computed(() => commitChain().length > 1 && selectedCommitSha() === null),
    selectAllCommits: () => selectedCommitSha.set(null),
    selectChainCommit: (sha: string) => {
      const entry = commitChain().find(c => c.sha === sha) ?? null;
      selectedCommitSha.set(sha);
      commitDetail.set(entry ? { commit: entry, files } : null);
    },
    selectDiffPath: () => undefined,
    refresh: () => undefined,
  };
}

function makeReview(overrides?: Partial<CodeReviewListEntry>): CodeReviewListEntry {
  return {
    fileName: 'code-review-2026-06-09.md',
    verdict: 'pass',
    summary: 'No blocking issues found.',
    model: 'claude-opus-4-8',
    cliType: 'claude',
    commit: commits[1].sha,
    runAt: '2026-06-09T11:00:00Z',
    ...overrides,
  };
}

describe('GitPaneComponent', () => {
  beforeEach(() => {
    localStorage.removeItem('taskboard.gitPane.commitGroupCollapsed');
    localStorage.removeItem('taskboard.gitPane.headCollapsed');
    localStorage.removeItem('taskboard.gitPane.diffViewMode');
    localStorage.removeItem('taskboard.gitPane.treeWidth');
  });

  function mountGit(git: ReturnType<typeof makeGitPaneMock>) {
    return TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();
  }

  it('renders the multi-commit group collapsed by default without a duplicate aggregate header', async () => {
    const git = makeGitPaneMock();
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const toggle = root.querySelector<HTMLElement>('[data-testid="git-commit-group-toggle"]');
    expect(toggle?.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('[data-testid="git-commit-chain"]')).toBeNull();
    expect(root.querySelector('[data-testid="git-commit-aggregate-header"]')).toBeNull();
    expect(root.textContent).toContain('All 2 commits');
  });

  it('shows one aggregate selector when the commit group is expanded', async () => {
    const git = makeGitPaneMock();
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="git-commit-group-toggle"]')?.click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-commit-chain"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="git-commit-chain-all"]')?.textContent).toContain('All 2 commits');
    expect(root.querySelector('[data-testid="git-commit-aggregate-header"]')).toBeNull();
  });

  it('shows compact date and time so commit rows from different days are distinguishable', async () => {
    const git = makeGitPaneMock();
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="git-commit-group-toggle"]')?.click();
    fixture.detectChanges();

    const meta = root.querySelectorAll<HTMLElement>('[data-testid="git-commit-chain-meta"]');
    const firstTimeOnly = formatTime(commits[0].at);
    const secondTimeOnly = formatTime(commits[1].at);
    expect(meta).toHaveLength(2);
    expect(meta[0].textContent?.trim()).toBe(`1f · ${formatCompactDateTime(commits[0].at)}`);
    expect(meta[1].textContent?.trim()).toBe(`1f · ${formatCompactDateTime(commits[1].at)}`);
    expect(firstTimeOnly).toBe(secondTimeOnly);
    expect(meta[0].textContent?.trim()).not.toBe(`1f · ${firstTimeOnly}`);
    expect(meta[1].textContent?.trim()).not.toBe(`1f · ${secondTimeOnly}`);
    expect(meta[0].textContent).not.toBe(meta[1].textContent);
  });

  it('shows a landed-status skeleton while provenance is still loading, then the ladder (AGT-2006)', async () => {
    const git = makeGitPaneMock();
    git.provenanceLoaded.set(false);
    await mountGit(git);

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    // Loading: the skeleton placeholder stands in for the ladder.
    expect(root.querySelector('[data-testid="git-landed-ladder-loading"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="git-landed-ladder"]')).toBeNull();

    // Provenance resolves -> the real ladder replaces the skeleton.
    git.provenance.set({
      branch: 'task/demo-task',
      base: 'base0000',
      transitions: [],
      merge: null,
      landedState: 'merged-to-develop',
      ladder: {
        branch: 'task/demo-task',
        branchTip: 'tip00000',
        integrationBranch: 'develop',
        integrationHead: 'devhead0',
        mergedToIntegration: true,
        releaseBranch: 'main',
        releaseHead: 'mainhead',
        releasedToRelease: false,
      },
      commits: [],
    } as TaskProvenanceView);
    git.provenanceLoaded.set(true);
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-landed-ladder-loading"]')).toBeNull();
    expect(root.querySelector('[data-testid="git-landed-ladder"]')).not.toBeNull();
  });

  it('gates a large selected diff until the operator reveals it', async () => {
    const git = makeGitPaneMock();
    git.diffText.set(Array.from({ length: LARGE_DIFF_LINE_THRESHOLD }, (_, i) => `+line ${i}`).join('\n'));
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const gated = root.querySelector('[data-testid="git-diff-gated"]');
    expect(gated?.textContent).toContain('src/one.ts');
    expect(gated?.textContent).toContain('Show diff');
    expect(root.querySelector('[data-testid="git-diff"]')).toBeNull();

    root.querySelector<HTMLButtonElement>('[data-testid="git-diff-show"]')?.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.diffGated()).toBe(false);
    expect(root.querySelector('[data-testid="git-diff-gated"]')).toBeNull();
  });

  it('labels the run-location as the task worktree when the live status is worktree-scoped (ASS-1731)', async () => {
    const git = makeGitPaneMock({ viewMode: 'worktree', status: worktreeStatus(true) });
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const location = root.querySelector('[data-testid="git-run-location"]');
    expect(location?.textContent?.trim()).toBe('(Worktree)');
    expect(location?.classList.contains('git-view__location--worktree')).toBe(true);
    expect(root.textContent).toContain('task/demo-task');
  });

  it('labels the run-location as the main checkout when the live status is not worktree-scoped (ASS-1731)', async () => {
    const git = makeGitPaneMock({ viewMode: 'worktree', status: worktreeStatus(false) });
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const location = root.querySelector('[data-testid="git-run-location"]');
    expect(location?.textContent?.trim()).toBe('(Haupt-Checkout)');
    expect(location?.classList.contains('git-view__location--worktree')).toBe(false);
  });

  it('renders a code-review rating badge on the commit line and jumps to the review on click (AGT-1995)', async () => {
    const git = makeGitPaneMock({ codeReviews: [makeReview({ verdict: 'concerns', summary: 'Two nits worth a look.' })] });
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    // Pin the view to a single commit so the commit line (and its badge) render.
    git.selectChainCommit(commits[1].sha);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const badge = root.querySelector<HTMLButtonElement>('[data-testid="git-commit-review-badge"]');
    expect(badge).not.toBeNull();
    expect(badge?.getAttribute('data-verdict')).toBe('concerns');
    expect(badge?.textContent).toContain('Concerns');

    // Clicking asks the shared layout service to reveal + focus the Code
    // Review tab of the prompt pane.
    const layout = TestBed.inject(LayoutPanesService);
    badge?.click();
    expect(layout.requestedPromptTab()).toBe('code-review');
    expect(layout.panesVisible().prompt).toBe(true);
  });

  it('shows no rating badge when no review matches the shown commit (AGT-1995)', async () => {
    const git = makeGitPaneMock({ codeReviews: [makeReview({ commit: 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef' })] });
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    git.selectChainCommit(commits[1].sha);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="git-commit-review-badge"]')).toBeNull();
  });

  it('defaults the diff layout to side-by-side and toggles to unified, persisting the choice', async () => {
    const git = makeGitPaneMock();
    await mountGit(git);

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const toggle = root.querySelector<HTMLButtonElement>('[data-testid="git-diff-mode-toggle"]');
    expect(toggle).not.toBeNull();
    // Default: side-by-side, pressed.
    expect(fixture.componentInstance.diffViewMode()).toBe('side-by-side');
    expect(toggle?.getAttribute('aria-pressed')).toBe('true');
    expect(toggle?.textContent?.trim()).toBe('Side-by-side');

    toggle?.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.diffViewMode()).toBe('line-by-line');
    expect(toggle?.getAttribute('aria-pressed')).toBe('false');
    expect(toggle?.textContent?.trim()).toBe('Unified');
    expect(localStorage.getItem('taskboard.gitPane.diffViewMode')).toBe('line-by-line');
  });

  it('restores the persisted unified diff layout on construction', async () => {
    localStorage.setItem('taskboard.gitPane.diffViewMode', 'line-by-line');
    const git = makeGitPaneMock();
    await mountGit(git);

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    expect(fixture.componentInstance.diffViewMode()).toBe('line-by-line');
    const root = fixture.nativeElement as HTMLElement;
    const toggle = root.querySelector<HTMLButtonElement>('[data-testid="git-diff-mode-toggle"]');
    expect(toggle?.getAttribute('aria-pressed')).toBe('false');
  });

  it('collapses the whole commit-meta head with one toggle and remembers the state', async () => {
    const git = makeGitPaneMock();
    await mountGit(git);

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    // Expanded by default: the head toggle and the commit group are present.
    const head = root.querySelector<HTMLButtonElement>('[data-testid="git-head-collapse-toggle"]');
    expect(head).not.toBeNull();
    expect(head?.getAttribute('aria-expanded')).toBe('true');
    expect(root.querySelector('[data-testid="git-commit-group"]')).not.toBeNull();

    head?.click();
    fixture.detectChanges();

    // Collapsed: the meta block is gone, only the compact summary strip stays.
    expect(fixture.componentInstance.headCollapsed()).toBe(true);
    expect(head?.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('[data-testid="git-commit-group"]')).toBeNull();
    expect(root.querySelector('[data-testid="git-head-summary"]')).not.toBeNull();
    expect(localStorage.getItem('taskboard.gitPane.headCollapsed')).toBe('1');
  });

  it('renders the draggable tree splitter carrying the persisted width', async () => {
    localStorage.setItem('taskboard.gitPane.treeWidth', '360');
    const git = makeGitPaneMock();
    await mountGit(git);

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const splitter = root.querySelector('[data-testid="git-tree-splitter"]');
    expect(splitter).not.toBeNull();
    expect(splitter?.getAttribute('role')).toBe('separator');
    const splitBody = root.querySelector<HTMLElement>('.git-view__split-body');
    expect(splitBody?.style.getPropertyValue('--git-tree-width')).toBe('360px');
  });

  it('offers a Preview toggle only for md/html files and renders the markdown preview (AGT-2008)', async () => {
    const git = makeGitPaneMock({
      selectedDiffPath: 'docs/start/README.md',
      previewOnLoad: { content: '# Hello preview' },
    });
    await mountGit(git);

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const toggle = root.querySelector<HTMLButtonElement>('[data-testid="git-preview-toggle"]');
    expect(toggle).not.toBeNull();
    expect(toggle?.textContent?.trim()).toBe('Preview');
    // Diff shown by default; the preview surface is absent.
    expect(root.querySelector('[data-testid="git-preview"]')).toBeNull();

    toggle?.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.previewActive()).toBe(true);
    expect(toggle?.textContent?.trim()).toBe('Diff');
    // Preview surface takes over; the raw diff + layout toggle step aside.
    expect(root.querySelector('[data-testid="git-preview"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="git-preview-markdown"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="git-diff-mode-toggle"]')).toBeNull();
  });

  it('renders the html preview in a script-enabled opaque-origin sandbox (AGT-2008)', async () => {
    const git = makeGitPaneMock({
      selectedDiffPath: 'site/index.html',
      previewOnLoad: { content: '<h1>Hi</h1>' },
    });
    await mountGit(git);

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="git-preview-toggle"]')?.click();
    fixture.detectChanges();

    const frame = root.querySelector<HTMLIFrameElement>('[data-testid="git-preview-html"]');
    expect(frame).not.toBeNull();
    expect(frame?.getAttribute('sandbox')).toBe('allow-scripts');
    expect(frame?.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect(frame?.getAttribute('srcdoc') ?? '').toContain('<h1>Hi</h1>');
  });

  it('shows no Preview toggle for a non-previewable file', async () => {
    const git = makeGitPaneMock({ selectedDiffPath: 'src/one.ts' });
    await mountGit(git);

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="git-preview-toggle"]')).toBeNull();
  });

  it('previewKindOf classifies md/html and rejects others', () => {
    expect(previewKindOf('README.md')).toBe('markdown');
    expect(previewKindOf('docs/guide.markdown')).toBe('markdown');
    expect(previewKindOf('a/index.html')).toBe('html');
    expect(previewKindOf('page.htm')).toBe('html');
    expect(previewKindOf('DIR/File.HTML')).toBe('html');
    expect(previewKindOf('src/app.ts')).toBeNull();
    expect(previewKindOf('LICENSE')).toBeNull();
    expect(previewKindOf(null)).toBeNull();
  });

});
