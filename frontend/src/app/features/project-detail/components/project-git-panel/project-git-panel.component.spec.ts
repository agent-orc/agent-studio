import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type { GitGraphCommit, GitProjectInventory } from '../../../git';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { loadDiff2Html } from '../../../../utils/diff2html-lazy';
import { ProjectGitPanelComponent } from './project-git-panel.component';

beforeAll(async () => {
  await loadDiff2Html();
});

const COMMIT_SHA = 'c'.repeat(40);

function graphCommit(overrides: Partial<GitGraphCommit> = {}): GitGraphCommit {
  return {
    sha: COMMIT_SHA,
    shortSha: 'ccccccc',
    parentShas: ['a'.repeat(40)],
    authorDateUtc: '2026-07-02T10:00:00Z',
    author: 'dev',
    subject: 'feat: add thing',
    filesChanged: 1,
    added: 3,
    removed: 1,
    refs: [{ name: 'task/1', kind: 'branch', isRemote: false }],
    tasks: [{ taskKey: 'Demo::task-1', key: 'AGT-1', title: 'Add thing', lane: '3-progress' }],
    presence: {
      inIntegration: true,
      inRelease: false,
      integrationBranch: 'develop',
      releaseBranch: 'main',
    },
    deployments: [{ target: 'runner', sha: COMMIT_SHA, shortSha: 'ccccccc' }],
    ...overrides,
  };
}

function inventoryFixture(overrides: Partial<GitProjectInventory> = {}): GitProjectInventory {
  return {
    projectName: 'Demo',
    repositoryPath: 'C:/repo/demo',
    isRepo: true,
    currentBranch: 'main',
    worktrees: [
      {
        path: 'C:/repo/demo',
        branch: 'main',
        headSha: 'a'.repeat(40),
        headShortSha: 'aaaaaaa',
        isPrimary: true,
        isDetached: false,
        isBare: false,
      },
    ],
    branches: [
      {
        name: 'main',
        category: 'main',
        tipSha: COMMIT_SHA,
        tipShortSha: 'ccccccc',
        isCurrent: true,
        upstream: 'origin/main',
        ahead: 0,
        behind: 0,
        lastCommitSubject: 'feat: add thing',
        lastCommitAtUtc: '2026-07-02T10:00:00Z',
        worktreePath: 'C:/repo/demo',
        isLocal: true,
        hasRemote: true,
      },
      {
        name: 'task/1',
        category: 'task',
        tipSha: 'b'.repeat(40),
        tipShortSha: 'bbbbbbb',
        isCurrent: false,
        upstream: null,
        ahead: 2,
        behind: 0,
        lastCommitSubject: 'task work',
        lastCommitAtUtc: '2026-07-02T00:00:00Z',
        worktreePath: null,
        isLocal: true,
        hasRemote: false,
      },
    ],
    recentCommits: [],
    history: {
      offset: 0,
      pageSize: 50,
      nextOffset: 1,
      hasMore: true,
      commits: [graphCommit()],
    },
    activeCheckouts: [{
      task: { taskKey: 'Demo::task-1', key: 'AGT-1', title: 'Add thing', lane: '3-progress' },
      branch: 'task/1',
      headSha: 'b'.repeat(40),
      location: 'remote',
      runner: 'agent-runner-01',
      worktreePath: null,
      activeSince: '2026-07-02T09:00:00Z',
    }],
    deployments: [{ target: 'runner', sha: COMMIT_SHA, shortSha: 'ccccccc' }],
    error: null,
    ...overrides,
  };
}

function setup() {
  const openTaskKey = vi.fn(() => true);
  TestBed.configureTestingModule({
    imports: [ProjectGitPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: TaskReferenceNavigationService, useValue: { openTaskKey } },
    ],
  });
  const fixture = TestBed.createComponent(ProjectGitPanelComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.detectChanges();
  return {
    fixture,
    http: TestBed.inject(HttpTestingController),
    root: fixture.nativeElement as HTMLElement,
    openTaskKey,
  };
}

describe('ProjectGitPanelComponent', () => {
  it('opens commit details on selection and closes them on a second click', () => {
    const { fixture, http, root } = setup();
    http.expectOne(request => request.url === '/api/git/inventory').flush(inventoryFixture());
    fixture.detectChanges();

    const row = root.querySelector<HTMLElement>('[data-testid="git-commit-row"]')!;
    row.click();
    fixture.detectChanges();
    http.expectOne(request => request.url === '/api/git/project-commit/files')
      .flush({ sha: COMMIT_SHA, files: [{ status: 'M', path: 'src/thing.ts', added: 3, removed: 1 }] });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-commit-details"]')?.textContent).toContain(COMMIT_SHA);
    expect(root.querySelector('[data-testid="git-commit-details"]')?.textContent).toContain('src/thing.ts');
    row.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="git-commit-details"]')).toBeNull();
    http.verify();
  });

  it('renders active checkouts and semantic chips beside the enriched commit graph', () => {
    const { fixture, http, root, openTaskKey } = setup();
    http.expectOne(request => request.url === '/api/git/inventory').flush(inventoryFixture());
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-repo-path"]')?.textContent).toContain('C:/repo/demo');
    expect(root.querySelector('[data-testid="git-tree-group-active"]')?.textContent).toContain('remote');
    expect(root.querySelector('[data-testid="git-tree-group-worktrees"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="git-tree-group-integration"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="git-tree-group-task"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="git-history"]')?.textContent).toContain('feat: add thing');
    const commitRow = root.querySelector<HTMLElement>('[data-testid="git-commit-row"]')!;
    const chips = [...commitRow.querySelectorAll<HTMLElement>('[data-tone]')]
      .map(chip => chip.textContent?.trim());
    expect(chips).toEqual(['Integrated · develop', 'Deployed · runner', 'AGT-1', 'In progress']);
    commitRow.querySelector<HTMLButtonElement>('[data-kind="task"]')!.click();
    expect(openTaskKey).toHaveBeenCalledWith('Demo::task-1');
    expect(root.querySelector('[data-testid="git-cleanup"]')).toBeNull();
    http.verify();
  });

  it('loads files and a diff only after the explicit changes action', () => {
    const { fixture, http, root } = setup();
    http.expectOne(request => request.url === '/api/git/inventory').flush(inventoryFixture());
    fixture.detectChanges();
    http.expectNone('/api/git/project-commit/files');

    root.querySelector<HTMLButtonElement>('button[aria-label="Inspect changes in ccccccc"]')!.click();
    fixture.detectChanges();
    http.expectOne(request =>
      request.url === '/api/git/project-commit/files' && request.params.get('sha') === COMMIT_SHA,
    ).flush({ sha: COMMIT_SHA, files: [{ status: 'M', path: 'src/thing.ts', added: 3, removed: 1 }] });
    fixture.detectChanges();
    http.expectOne(request =>
      request.url === '/api/git/project-commit/diff' && request.params.get('path') === 'src/thing.ts',
    ).flush({ diff: 'diff --git a/src/thing.ts b/src/thing.ts\n+added\n', hasDiff: true, emptyReason: null });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-changes"]')?.textContent).toContain('feat: add thing');
    expect(root.querySelectorAll('[data-testid="git-file-row"]')).toHaveLength(1);
    expect(fixture.componentInstance.inspectedCommit()?.sha).toBe(COMMIT_SHA);
    http.verify();
  });

  it('appends an older page without duplicating commits', () => {
    const { fixture, http, root } = setup();
    http.expectOne(request => request.url === '/api/git/inventory').flush(inventoryFixture());
    fixture.detectChanges();

    root.querySelector<HTMLButtonElement>('[data-testid="git-history-load-more"]')!.click();
    fixture.detectChanges();
    http.expectOne(request =>
      request.url === '/api/git/history' && request.params.get('offset') === '1',
    ).flush({
      offset: 1,
      pageSize: 50,
      nextOffset: null,
      hasMore: false,
      commits: [
        graphCommit(),
        graphCommit({ sha: 'd'.repeat(40), shortSha: 'ddddddd', subject: 'older commit' }),
      ],
    });
    fixture.detectChanges();

    expect(root.querySelectorAll('[data-testid="git-commit-row"]')).toHaveLength(2);
    expect(root.textContent).toContain('older commit');
    http.verify();
  });

  it('shows the project-level empty state for a non-repository', () => {
    const { fixture, http, root } = setup();
    http.expectOne(request => request.url === '/api/git/inventory').flush(inventoryFixture({
      isRepo: false,
      repositoryPath: 'C:/repo/demo',
      currentBranch: null,
      worktrees: [],
      branches: [],
      recentCommits: [],
      history: null,
      error: 'Not a git repository: C:/repo/demo',
    }));
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-empty"]')?.textContent).toContain('Not a git repository');
    expect(root.querySelector('[data-testid="git-tree"]')).toBeNull();
    http.verify();
  });
});
