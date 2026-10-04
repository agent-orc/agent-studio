import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { ProjectOverviewDashboardComponent } from './project-overview-dashboard';

describe('ProjectOverviewDashboardComponent', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('renders operator metrics, deployment, Wiki, and planning without machine plumbing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectOverviewDashboardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const tasks = TestBed.inject(TaskService);
    tasks.jobs.set([planningTask()]);
    const fixture = TestBed.createComponent(ProjectOverviewDashboardComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    const openedTasks: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe(task => openedTasks.push(task));
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectNone('/api/projects/Demo%20Project/throughput');
    http.expectOne('/api/projects/Demo%20Project/token-usage/summary').flush(tokenSummary());
    http.expectOne('/api/projects/Demo%20Project/deployment/summary').flush({
      project: 'Demo Project', available: true, reason: null,
      source: 'logs/stable-restarts.jsonl', pendingCount: 3,
      pendingCommits: [
        { sha: '111111111111', shortSha: '1111111', subject: 'Ship operator dashboard', authorDateUtc: '2026-07-11T10:00:00Z' },
        { sha: '222222222222', shortSha: '2222222', subject: 'Add URL start state', authorDateUtc: '2026-07-11T09:00:00Z' },
        { sha: '333333333333', shortSha: '3333333', subject: 'Refresh Wiki pulse', authorDateUtc: '2026-07-11T08:00:00Z' },
      ],
      lastDeployment: {
        at: '2026-07-10T10:00:00Z', status: 'ok', headBefore: 'aaaaaaa', headAfter: 'bbbbbbb',
        durationSeconds: 42, jobsSinceLastRestart: 4, reviewCountAfter: 12, commits: [
          { sha: 'aaaaaaaaaaaa', shortSha: 'aaaaaaa', subject: 'Previously deployed change', authorDateUtc: '2026-07-10T09:00:00Z' },
        ],
      },
    });
    http.expectOne('/api/projects/Demo%20Project/wiki/pulse?feedLimit=6').flush(wikiPulse());
    http.expectOne('/api/projects/Demo%20Project/snapshot').flush(snapshot());
    http.expectOne(request => request.url === '/api/git/inventory' && request.params.get('project') === 'Demo Project').flush(gitInventory());
    http.expectOne('/api/projects/Demo%20Project/visual-evidence').flush(evidenceQueue());
    http.expectOne('/api/v1/workspaces').flush([{ id: 'ws', displayName: 'Workspace', projects: [{
      id: 'PROJ-1', displayName: 'Demo Project', workspaceId: 'ws', storageLocation: 'C:/tasks/demo',
      sortOrder: 0, archived: false, urls: [],
    }] }]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-overview-throughput"]')).toBeNull();
    expect(host.textContent).not.toContain('Delivered tasks');
    expect(host.querySelector('[data-testid="project-overview-tokens-24h"]')?.textContent).toContain('124K');
    expect(host.querySelector('[data-testid="project-overview-tokens-7d"]')?.textContent).toContain('831K');
    expect(host.querySelector('[data-testid="project-overview-deployment"]')?.textContent).toContain('3 changes ready to deploy');
    expect(host.querySelector('[data-testid="project-overview-last-deployment-details"]')?.textContent).toContain('Previously deployed change');
    expect(host.querySelector('[data-testid="project-overview-wiki"]')?.textContent).toContain('Operator dashboard concept');
    expect(host.querySelector('[data-testid="project-overview-planning-agt-2200"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-overview-evidence-count"]')?.textContent).toContain('4 unseen');
    expect(host.querySelectorAll('[data-testid^="project-overview-evidence-visual-screenshot-demo-"]')).toHaveLength(4);
    expect(host.querySelector('[data-testid="project-overview-evidence-visual-screenshot-demo-5"]')).toBeNull();
    const branchState = host.querySelector('[data-testid="project-overview-remote-truth"]')?.textContent ?? '';
    expect(branchState).toContain('2 to push');
    expect(branchState).toContain('3 to pull');
    expect(branchState).toContain('No upstream · local-only, remote comparison unavailable');
    expect(host.querySelector('[data-testid="project-overview-branch-task-agt-2200"]')).toBeTruthy();
    host.querySelector<HTMLButtonElement>('[data-testid="project-overview-planning-agt-2200"]')!.click();
    expect(openedTasks).toEqual([{ jobId: 'agt-2200', watchPath: 'C:/tasks/demo' }]);

    const text = host.textContent ?? '';
    expect(text).not.toContain('Watch path');
    expect(text).not.toContain('Working directory');
    expect(text).not.toContain('Clean context');
    expect(text).not.toContain('Project sessions');
    http.verify();
  });

  it('routes compact blocks to their existing detail rails', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectOverviewDashboardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectOverviewDashboardComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    const rails: string[] = [];
    fixture.componentInstance.openRail.subscribe(rail => rails.push(rail));
    fixture.detectChanges();
    flushEmpty(TestBed.inject(HttpTestingController));
    fixture.detectChanges();

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="project-overview-open-token-usage"]')!.click();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="project-overview-open-wiki"]')!.click();

    expect(rails).toEqual(['token-usage', 'wiki']);
  });

  it('swaps the recent Wiki feed when its conditional poll returns a new ETag', async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({
      imports: [ProjectOverviewDashboardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectOverviewDashboardComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    flushEmpty(http);
    fixture.detectChanges();
    const originalWiki = fixture.componentInstance.wiki();

    await vi.advanceTimersByTimeAsync(15_000);
    const poll = http.expectOne('/api/projects/Demo%20Project/wiki/recent?limit=6');
    expect(poll.request.headers.has('If-None-Match')).toBe(false);
    poll.flush({
      projectName: 'Demo Project',
      baseDir: 'C:/repo/docs',
      exists: true,
      edits: [{
        relPath: 'concepts/live-refresh.md',
        title: 'Wiki live refresh',
        author: 'Robert',
        authorDateUtc: '2026-07-26T10:00:00Z',
        sha: 'def',
        shortSha: 'def',
        subject: 'Refresh Wiki live',
      }],
    }, { headers: { ETag: '"recent-v2"' } });
    fixture.detectChanges();

    expect(fixture.componentInstance.wiki()).not.toBe(originalWiki);
    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="project-overview-wiki"]')?.textContent)
      .toContain('Wiki live refresh');
    fixture.destroy();
    vi.useRealTimers();
  });

  it('pauses the recent Wiki poll while the document is hidden', async () => {
    vi.useFakeTimers();
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
    await TestBed.configureTestingModule({
      imports: [ProjectOverviewDashboardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectOverviewDashboardComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    flushEmpty(http);

    await vi.advanceTimersByTimeAsync(30_000);
    http.expectNone('/api/projects/Demo%20Project/wiki/recent?limit=6');

    fixture.destroy();
    hidden.mockRestore();
    vi.useRealTimers();
  });
});

function planningTask(): TaskInfo {
  return {
    id: 'agt-2200', taskKey: 'AGT-2200', key: 'AGT-2200', title: 'Plan deployment history',
    state: '2-ready', order: 0, agent: 'codex', createdAt: '2026-07-11T08:00:00Z',
    watchPath: 'C:/tasks/demo', projectName: 'Demo Project', folderPath: 'C:/tasks/demo/2-ready/agt-2200',
    lastActivity: '2026-07-11T09:00:00Z', sessionName: null, model: null, cliType: 'codex',
    useOwnSession: null, lastUsage: null, execution: null, commit: null, mode: 'planning',
  } as TaskInfo;
}

function tokenSummary() {
  return {
    project: 'Demo Project', hasData: true,
    lifetimeTotalTokens: 1_400_000, lifetimeJobTokens: 1_000_000,
    lifetimeSupportingTokens: 250_000, lifetimeOrchestratorTokens: 150_000, lifetimeCalls: 90,
    last24hTotalTokens: 124_000, last24hJobTokens: 90_000,
    last24hSupportingTokens: 20_000, last24hOrchestratorTokens: 14_000, last24hCalls: 8,
    last7dTotalTokens: 831_000, last7dJobTokens: 620_000,
    last7dSupportingTokens: 130_000, last7dOrchestratorTokens: 81_000, last7dCalls: 44,
    firstActivity: '2026-07-01T00:00:00Z', lastActivity: '2026-07-11T10:00:00Z',
    fetchedAt: '2026-07-11T12:00:00Z', disclaimer: 'Measured usage.',
  };
}

function wikiPulse() {
  return {
    projectName: 'Demo Project', baseDir: 'C:/repo/docs', exists: true, generatedAtUtc: '2026-07-11T12:00:00Z',
    feed: { available: true, reason: null, items: [{
      relPath: 'concepts/operator-dashboard.md', title: 'Operator dashboard concept', author: 'Robert',
      authorDateUtc: '2026-07-11T10:30:00Z', sha: 'abc', shortSha: 'abc', subject: 'Add concept',
      areaSlug: 'concepts', areaTitle: 'Concepts', taskKey: 'AGT-2105',
    }] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [], counts: { fresh: 1, aging: 0, stale: 0, graded: 1 } },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  };
}

function snapshot() {
  return {
    project: 'Demo Project', capturedAt: '2026-07-11T12:00:00Z',
    paths: { path: 'C:/tasks/demo', rootPath: 'C:/repo', repositoryPath: 'C:/repo' },
    settings: { autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'on-completed', runnerMode: 'manual', orchestratorModel: null },
    runnerStatus: null, orchestratorLogTail: [], orchestratorSession: null,
    reviewDecisionsPending: [], runnerPendingDecisions: [], publishTargets: [],
    queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
  };
}

function flushEmpty(http: HttpTestingController): void {
  http.expectNone('/api/projects/Demo%20Project/throughput');
  http.expectOne('/api/projects/Demo%20Project/token-usage/summary').flush(tokenSummary());
  http.expectOne('/api/projects/Demo%20Project/deployment/summary').flush({
    project: 'Demo Project', available: false, reason: 'No history.', source: 'logs/stable-restarts.jsonl',
    lastDeployment: null, pendingCount: null, pendingCommits: [],
  });
  http.expectOne('/api/projects/Demo%20Project/wiki/pulse?feedLimit=6').flush({ ...wikiPulse(), feed: { available: true, reason: null, items: [] } });
  http.expectOne('/api/projects/Demo%20Project/snapshot').flush(snapshot());
  http.expectOne(request => request.url === '/api/git/inventory' && request.params.get('project') === 'Demo Project').flush(gitInventory());
  http.expectOne('/api/projects/Demo%20Project/visual-evidence').flush({
    project: 'Demo Project', capturedAt: '2026-07-11T12:00:00Z', unseenCount: 0, items: [],
  });
  http.expectOne('/api/v1/workspaces').flush([]);
}

function gitInventory() {
  return {
    projectName: 'Demo Project', repositoryPath: 'C:/repo', isRepo: true, currentBranch: 'develop',
    worktrees: [], recentCommits: [], error: null,
    branches: [
      { name: 'main', category: 'main', tipSha: 'a'.repeat(40), tipShortSha: 'aaaaaaa', isCurrent: false, upstream: 'origin/main', ahead: 0, behind: 0, lastCommitSubject: 'released', lastCommitAtUtc: '2026-07-11T08:00:00Z', worktreePath: null },
      { name: 'develop', category: 'develop', tipSha: 'b'.repeat(40), tipShortSha: 'bbbbbbb', isCurrent: true, upstream: 'origin/develop', ahead: 2, behind: 0, lastCommitSubject: 'integrated', lastCommitAtUtc: '2026-07-11T11:00:00Z', worktreePath: 'C:/repo' },
      { name: 'task/AGT-2200-plan-deployment-history', category: 'task', tipSha: 'c'.repeat(40), tipShortSha: 'ccccccc', isCurrent: false, upstream: 'origin/task/AGT-2200-plan-deployment-history', ahead: 0, behind: 3, lastCommitSubject: 'planning', lastCommitAtUtc: '2026-07-11T10:00:00Z', worktreePath: null },
      { name: 'task/LOCAL-1', category: 'task', tipSha: 'd'.repeat(40), tipShortSha: 'ddddddd', isCurrent: false, upstream: null, ahead: 0, behind: 0, lastCommitSubject: 'local only', lastCommitAtUtc: '2026-07-11T09:00:00Z', worktreePath: null },
    ],
  };
}

function evidenceQueue() {
  return {
    project: 'Demo Project', capturedAt: '2026-07-11T12:00:00Z', unseenCount: 5,
    items: Array.from({ length: 5 }, (_, index) => ({
      id: `visual-screenshot-demo-${index + 1}`, jobId: 'agt-2199', jobTitle: 'Delivered UI', watchPath: 'C:/tasks/demo',
      fileName: `overview-${index + 1}--real.png`, relativePath: `results/overview-${index + 1}--real.png`, url: '/shot.png',
      caption: `Delivered overview ${index + 1}`, testStatus: 'passed', source: 'real',
      capturedAt: `2026-07-11T${String(11 - index).padStart(2, '0')}:00:00Z`, reviewStatus: 'unseen',
    })),
  };
}
