import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { effect, provideZonelessChangeDetection, signal } from '@angular/core';
import { GitPaneService, PROVENANCE_WAIT_MS } from './git-pane.service';
import { TaskState, type TaskInfo } from '../../../models/task.model';
import { GIT_FACTS_WARMING_RETRY_DELAYS_MS } from '../../../utils/git-facts-warming';

describe('GitPaneService hidden pane loading', () => {
  it('resolves provenance for a Human Review action while Overview is visible', async () => {
    await TestBed.configureTestingModule({ providers: [GitPaneService,
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      provideRouter([])] }).compileComponents();
    const service = TestBed.inject(GitPaneService);
    const http = TestBed.inject(HttpTestingController);
    service.setJob({ id: 'job-review', watchPath: '/wp', state: '5-human-review' } as TaskInfo, false);
    expect(service.provenanceLoaded()).toBe(false);
    http.expectOne(request => request.url.endsWith('/api/tasks/job-review/provenance')).flush(null);
    expect(service.provenanceLoaded()).toBe(true);
    http.expectNone(request => request.url.endsWith('/api/tasks/job-review/code-review/list'));
    http.verify();
  });
  it('waits for the pane to open and reuses the same task snapshot', async () => {
    await TestBed.configureTestingModule({ providers: [GitPaneService,
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      provideRouter([])] }).compileComponents();
    const service = TestBed.inject(GitPaneService);
    const http = TestBed.inject(HttpTestingController);
    const job = { id: 'job-x', watchPath: '/wp' } as TaskInfo;
    service.setJob(job, false);
    http.expectNone(() => true);
    service.setJob(job, true);
    http.expectOne(request => request.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
    http.expectOne(request => request.url.endsWith('/api/tasks/job-x/code-review/list')).flush({ entries: [] });
    service.setJob(job, false);
    service.setJob(job, true);
    http.expectNone(() => true);
    http.verify();
  });

  it('reports provenance as loading only while its request is in flight', async () => {
    await TestBed.configureTestingModule({ providers: [GitPaneService,
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      provideRouter([])] }).compileComponents();
    const service = TestBed.inject(GitPaneService);
    const http = TestBed.inject(HttpTestingController);
    const job = { id: 'job-y', watchPath: '/wp' } as TaskInfo;
    // A hidden pane defers provenance; Accept must not wait for it.
    service.setJob(job, false);
    expect(service.provenanceLoading()).toBe(false);
    service.setJob(job, true);
    expect(service.provenanceLoading()).toBe(true);
    http.expectOne(request => request.url.endsWith('/api/tasks/job-y/provenance')).flush(null);
    expect(service.provenanceLoading()).toBe(false);
    http.expectOne(request => request.url.endsWith('/api/tasks/job-y/code-review/list')).flush({ entries: [] });
    http.verify();
  });
});

describe('GitPaneService header provenance (AGT-3621)', () => {
  let service: GitPaneService;
  let http: HttpTestingController;
  const job = { id: 'job-hr', watchPath: '/wp' } as TaskInfo;
  const provenanceUrl = (request: { url: string }) => request.url.endsWith('/api/tasks/job-hr/provenance');

  beforeEach(async () => {
    await TestBed.configureTestingModule({ providers: [GitPaneService,
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      provideRouter([])] }).compileComponents();
    service = TestBed.inject(GitPaneService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => vi.useRealTimers());

  it('settles provenance while the Git pane stays hidden and does not refetch it when the pane opens', () => {
    service.setJob(job, false);
    expect(service.provenanceLoaded()).toBe(false);
    service.ensureProvenance();
    service.ensureProvenance();
    http.expectOne(provenanceUrl).flush(null);
    expect(service.provenanceLoaded()).toBe(true);
    service.setJob(job, true);
    http.expectNone(provenanceUrl);
    http.expectOne(request => request.url.endsWith('/api/tasks/job-hr/code-review/list')).flush({ entries: [] });
    http.verify();
  });

  it('loads provenance for a Human Review card while the Git pane is closed', () => {
    service.setJob({ ...job, state: TaskState.HumanReview } as TaskInfo, false);
    http.expectOne(provenanceUrl).flush(null);
    expect(service.provenanceLoaded()).toBe(true);
    http.verify();
  });

  it('stops waiting after the bounded wait when the provenance reply never arrives', () => {
    vi.useFakeTimers();
    service.setJob(job, false);
    service.ensureProvenance();
    const pending = http.expectOne(provenanceUrl);
    vi.advanceTimersByTime(PROVENANCE_WAIT_MS - 1);
    expect(service.provenanceLoaded()).toBe(false);
    vi.advanceTimersByTime(1);
    expect(service.provenanceLoaded()).toBe(true);
    expect(PROVENANCE_WAIT_MS).toBeLessThan(10_000);
    pending.flush({ landedState: 'released-to-main' });
    expect(service.provenance()?.landedState).toBe('released-to-main');
  });

  it('ignores a provenance reply for a job the view has left', () => {
    service.setJob(job, false);
    service.ensureProvenance();
    const stale = http.expectOne(provenanceUrl);
    service.setJob({ id: 'job-next', watchPath: '/wp' } as TaskInfo, false);
    stale.flush({ landedState: 'released-to-main' });
    expect(service.provenance()).toBeNull();
    expect(service.provenanceLoaded()).toBe(false);
  });
});

/**
 * Regression: clicking a file in the git tree must always end up showing
 * THAT file's diff. Diff fetches are async, so a slow response for an
 * earlier-clicked file used to land after the user had already selected a
 * different file and overwrite `diffText` — leaving the wrong diff under the
 * new file's highlight + path label. `selectDiffPath` now pins each request
 * to the path it was issued for and drops stale results.
 */
describe('GitPaneService.selectDiffPath (out-of-order responses)', () => {
  let service: GitPaneService;
  let http: HttpTestingController;

  const job = {
    id: 'job-x',
    watchPath: '/wp',
  } as unknown as TaskInfo;

  const diffReq = (path: string) =>
    http.expectOne(
      (r) => r.url.endsWith('/api/tasks/job-x/git/diff') && r.params.get('path') === path,
    );

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        GitPaneService,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    service = TestBed.inject(GitPaneService);
    http = TestBed.inject(HttpTestingController);
    service.setJob(job);
    // setJob eagerly loads the landed-ladder provenance and the code-review
    // listing. Drain both so the diff assertions below start from a clean
    // HTTP queue and `http.verify()` doesn't trip over them.
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush({ entries: [] });
  });

  afterEach(() => http.verify());

  it('keeps the newly selected file when an earlier request resolves late', () => {
    // Click A — slow round-trip, left pending.
    service.selectDiffPath('a/x.ts');
    const reqA = diffReq('a/x.ts');

    // Click B before A resolves — B is now the selected file.
    service.selectDiffPath('b/y.ts');
    const reqB = diffReq('b/y.ts');
    expect(service.selectedDiffPath()).toBe('b/y.ts');

    // B resolves first and is shown.
    reqB.flush(utf8Buffer('DIFF_B'));
    expect(service.diffText()).toBe('DIFF_B');

    // A resolves late — it must NOT clobber the visible diff for B.
    reqA.flush(utf8Buffer('DIFF_A'));
    expect(service.selectedDiffPath()).toBe('b/y.ts');
    expect(service.diffText()).toBe('DIFF_B');
  });

  it('still caches the stale result so re-selecting that file is instant', () => {
    service.selectDiffPath('a/x.ts');
    const reqA = diffReq('a/x.ts');
    service.selectDiffPath('b/y.ts');
    const reqB = diffReq('b/y.ts');

    reqB.flush(utf8Buffer('DIFF_B'));
    reqA.flush(utf8Buffer('DIFF_A')); // stale for display, but cached.

    // Re-select A: served from cache, no second round-trip, correct text.
    service.selectDiffPath('a/x.ts');
    http.expectNone((r) => r.url.endsWith('/api/tasks/job-x/git/diff') && r.params.get('path') === 'a/x.ts');
    expect(service.selectedDiffPath()).toBe('a/x.ts');
    expect(service.diffText()).toBe('DIFF_A');
  });
});

/**
 * Regression: the code-review listing feeds the commit-row rating badge via
 * the `commitReview` computed, which calls `codeReviews().find(...)`. The
 * listing contract is `{ entries: [...] }`, but a malformed body - notably a
 * bare `[]`, whose `.entries` is `Array.prototype.entries` (a truthy function)
 * - used to slip past `resp.entries ?? []` and land a function in the signal.
 * The next time a single commit was selected the computed threw
 * `find is not a function`, which aborted the git-pane's change-detection pass
 * and left the commit header half-rendered (empty file count, no message).
 * `loadCodeReviews` now trusts only an actual array.
 */
describe('GitPaneService.loadCodeReviews (defensive shape guard)', () => {
  let service: GitPaneService;
  let http: HttpTestingController;

  const commit = {
    sha: 'abcabcabcabcabcabcabcabcabcabcabcabcabca',
    shortSha: 'abcabca',
    message: 'only commit',
    filesChanged: 1,
    files: ['src/one.ts'],
    at: '2026-06-08T10:00:00Z',
  };
  const job = { id: 'job-x', watchPath: '/wp', commit } as unknown as TaskInfo;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        GitPaneService,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    service = TestBed.inject(GitPaneService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('coerces a malformed (bare-array) listing body to an empty list so the badge computed never throws', () => {
    service.setJob(job);
    // A single-commit job pins the detail view to that commit, so `commitReview`
    // reaches its `codeReviews().find(...)` call rather than short-circuiting.
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/commit')).flush({ commit, files: [] });
    // The footgun: a bare array whose `.entries` is a function.
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush([]);

    expect(Array.isArray(service.codeReviews())).toBe(true);
    expect(service.codeReviews()).toEqual([]);
    // The rating-badge computed reads codeReviews().find(...); it must not throw.
    expect(() => service.commitReview()).not.toThrow();
    expect(service.commitReview()).toBeNull();
  });

  it('keeps a well-formed { entries } listing and matches the shown commit', () => {
    service.setJob(job);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/commit')).flush({ commit, files: [] });
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush({
      entries: [
        { fileName: 'r.md', verdict: 'pass', summary: 'ok', model: 'm', cliType: 'claude', commit: commit.sha, runAt: '2026-06-08T11:00:00Z' },
      ],
    });

    expect(service.codeReviews()).toHaveLength(1);
    expect(service.commitReview()?.verdict).toBe('pass');
  });
});

describe('GitPaneService commit file request ownership', () => {
  let service: GitPaneService;
  let http: HttpTestingController;
  const commits = ['a', 'b'].map(letter => ({
    sha: letter.repeat(40), shortSha: letter.repeat(7), message: `Commit ${letter}`,
    filesChanged: 1, files: [`${letter}.ts`], at: '2026-11-27T00:00:00Z',
  }));
  const job = { id: 'files-job', watchPath: '/wp', commits, commit: commits[1] } as TaskInfo;
  const files = (name: string) => [{ status: 'M', path: `${name}.ts`, added: 1, removed: 0 }];
  const aggregate = () => http.expectOne(r => r.url.endsWith('/commits/files'));
  const perSha = (index: number) => http.expectOne(r => r.url.endsWith(`/commits/${commits[index].sha}/files`));
  const drainMetadata = () => {
    http.expectOne(r => r.url.endsWith('/provenance')).flush(null);
    http.expectOne(r => r.url.endsWith('/code-review/list')).flush({ entries: [] });
  };
  const drainDiff = () => http.expectOne(r => r.url.endsWith('/diff')).flush({ diff: 'selected diff' });
  const expectB = () => {
    expect(service.selectedCommitSha()).toBe(commits[1].sha);
    expect(service.commitDetail()?.commit).toEqual(commits[1]);
    expect(service.commitFiles()).toEqual(files('b'));
    expect(service.selectedDiffPath()).toBe('b.ts');
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({ providers: [GitPaneService,
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      provideRouter([])] }).compileComponents();
    service = TestBed.inject(GitPaneService);
    http = TestBed.inject(HttpTestingController);
    service.setJob(job);
    drainMetadata();
    aggregate().flush({ files: [] });
  });

  afterEach(() => {
    try { http.verify(); }
    finally { vi.useRealTimers(); TestBed.resetTestingModule(); }
  });

  for (const outcome of ['success', 'failure']) {
    it(`keeps B's header, SHA, files and path when A returns last with ${outcome}`, () => {
      service.selectChainCommit(commits[0].sha);
      const a = perSha(0);
      service.selectChainCommit(commits[1].sha);
      perSha(1).flush({ files: files('b') });
      drainDiff();
      expectB();
      if (outcome === 'success') a.flush({ files: files('a') });
      else a.flush('failed', { status: 500, statusText: 'Failed' });
      expectB();
    });
  }
  for (const mode of ['aggregate', 'commit', 'snapshot'] as const) {
    const start = () => {
      if (mode === 'snapshot') {
        service.setJob({ ...job, commits: [commits[0]], commit: commits[0] });
        drainMetadata();
        return http.expectOne(r => r.url.endsWith('/commit'));
      }
      service.selectChainCommit(commits[0].sha);
      if (mode === 'commit') return perSha(0);
      perSha(0).flush({ files: [] });
      service.selectAllCommits();
      return aggregate();
    };

    for (const populated of [false, true]) {
      it(`distinguishes loading from a confirmed ${populated ? 'populated' : 'empty'} ${mode} list`, () => {
        const pending = start();
        expect(service.commitFilesState()).toBe('loading');
        expect(service.commitFiles()).toEqual([]);
        expect(service.selectedDiffPath()).toBeNull();
        if (mode !== 'aggregate') expect(service.commitDetail()?.commit).toEqual(commits[0]);
        pending.flush({ commit: commits[0], files: populated ? files('a') : [] });
        expect(service.commitFilesState()).toBe('loaded');
        expect(service.commitFiles()).toEqual(populated ? files('a') : []);
        expect(service.selectedDiffPath()).toBe(populated ? 'a.ts' : null);
        if (populated) drainDiff();
      });
    }

    it(`retries only the selected ${mode} after a failure and retains its known header`, () => {
      start().flush('failed', { status: 500, statusText: 'Failed' });
      expect(service.commitFilesState()).toBe('error');
      expect(service.commitFiles()).toEqual([]);
      expect(service.selectedDiffPath()).toBeNull();
      if (mode !== 'aggregate') expect(service.commitDetail()?.commit).toEqual(commits[0]);
      service.retryCommitFiles();
      const retry = mode === 'aggregate' ? aggregate() : perSha(0);
      expect(retry.request.params.get('watchPath')).toBe('/wp');
      expect(service.commitFilesState()).toBe('loading');
      if (mode !== 'aggregate') expect(service.commitDetail()?.commit).toEqual(commits[0]);
      // Double clicks cannot enqueue overlapping retries.
      service.retryCommitFiles();
      http.expectNone(r => r.url.endsWith('/files'));
      retry.flush({ files: files('a') });
      drainDiff();
      expect(service.commitFilesState()).toBe('loaded');
      expect(service.selectedDiffPath()).toBe('a.ts');
    });

    it(`does not call an unavailable ${mode} file payload confirmed empty`, () => {
      vi.useFakeTimers();
      let pending = start();
      for (const delay of GIT_FACTS_WARMING_RETRY_DELAYS_MS) {
        pending.flush({ state: 'warming', files: [] });
        expect(service.commitFilesState()).toBe('loading');
        vi.advanceTimersByTime(delay);
        pending = mode === 'aggregate' ? aggregate()
          : mode === 'snapshot' ? http.expectOne(r => r.url.endsWith('/commit')) : perSha(0);
      }
      pending.flush({ state: 'warming', files: [] });
      expect(service.commitFilesState()).toBe('error');
      expect(service.selectedDiffPath()).toBeNull();
    });
  }

  for (const outcome of ['success', 'failure']) {
    it(`discards an aggregate ${outcome} after selecting a commit`, () => {
      service.selectChainCommit(commits[0].sha);
      perSha(0).flush({ files: [] });
      service.selectAllCommits();
      const stale = aggregate();
      service.selectChainCommit(commits[1].sha);
      perSha(1).flush({ files: files('b') });
      drainDiff();
      if (outcome === 'success') stale.flush({ files: files('a') });
      else stale.flush('failed', { status: 500, statusText: 'Failed' });
      expectB();
      expect(service.aggregateFiles()).toEqual([]);
      expect(service.commitFilesState()).toBe('loaded');
    });

    it(`discards a per-SHA ${outcome} after selecting aggregate mode`, () => {
      service.selectChainCommit(commits[0].sha);
      const stale = perSha(0);
      service.selectAllCommits();
      aggregate().flush({ files: files('all') });
      drainDiff();
      if (outcome === 'success') stale.flush({ files: files('a') });
      else stale.flush('failed', { status: 500, statusText: 'Failed' });
      expect(service.selectedCommitSha()).toBeNull();
      expect(service.commitDetail()).toBeNull();
      expect(service.commitFiles()).toEqual(files('all'));
      expect(service.selectedDiffPath()).toBe('all.ts');
      expect(service.commitFilesState()).toBe('loaded');
    });

    for (const identity of ['id', 'watchPath'] as const) {
      it(`ignores a per-SHA ${outcome} after switching task ${identity}, even with the same SHA`, () => {
        service.selectChainCommit(commits[1].sha);
        const stale = perSha(1);
        service.setJob({ ...job, [identity]: 'other' });
        drainMetadata();
        aggregate().flush({ files: [] });
        service.selectChainCommit(commits[1].sha);
        perSha(1).flush({ files: files('new-task') });
        drainDiff();
        if (outcome === 'success') stale.flush({ files: files('old-task') });
        else stale.flush('failed', { status: 500, statusText: 'Failed' });
        expect(service.selectedCommitSha()).toBe(commits[1].sha);
        expect(service.commitDetail()?.commit).toEqual(commits[1]);
        expect(service.commitFiles()).toEqual(files('new-task'));
        expect(service.selectedDiffPath()).toBe('new-task.ts');
        expect(service.commitFilesState()).toBe('loaded');
      });
    }

    for (const mode of ['aggregate', 'snapshot'] as const) {
      it(`ignores a ${mode} ${outcome} after leaving and reopening the same task`, () => {
        let stale;
        let reopened = job;
        if (mode === 'snapshot') {
          reopened = { ...job, commits: [commits[0]], commit: commits[0] };
          service.setJob(reopened);
          drainMetadata();
          stale = http.expectOne(r => r.url.endsWith('/commit'));
        } else {
          service.selectChainCommit(commits[0].sha);
          perSha(0).flush({ files: [] });
          service.selectAllCommits();
          stale = aggregate();
        }
        service.setJob(null);
        service.setJob(reopened);
        drainMetadata();
        const current = mode === 'snapshot' ? http.expectOne(r => r.url.endsWith('/commit')) : aggregate();
        current.flush({ commit: commits[0], files: files('current') });
        drainDiff();
        if (outcome === 'success') stale.flush({ commit: commits[0], files: files('stale') });
        else stale.flush('failed', { status: 500, statusText: 'Failed' });
        expect(service.selectedCommitSha()).toBe(mode === 'snapshot' ? commits[0].sha : null);
        expect(service.commitDetail()?.commit ?? null).toEqual(mode === 'snapshot' ? commits[0] : null);
        expect(service.commitFiles()).toEqual(files('current'));
        expect(service.selectedDiffPath()).toBe('current.ts');
        expect(service.commitFilesState()).toBe('loaded');
      });
    }
  }

  it('rejects an older request when the same SHA is selected again', () => {
    service.selectChainCommit(commits[0].sha);
    const oldA = perSha(0);
    service.selectChainCommit(commits[1].sha);
    const b = perSha(1);
    service.selectChainCommit(commits[0].sha);
    perSha(0).flush({ files: files('current-a') });
    drainDiff();
    oldA.flush({ files: files('stale-a') });
    b.flush('failed', { status: 500, statusText: 'Failed' });
    expect(service.commitFiles()).toEqual(files('current-a'));
    expect(service.selectedDiffPath()).toBe('current-a.ts');
    expect(service.commitFilesState()).toBe('loaded');
  });

  it('keeps the current read pending when a superseded read fails', () => {
    service.selectChainCommit(commits[0].sha);
    const a = perSha(0);
    service.selectChainCommit(commits[1].sha);
    const b = perSha(1);
    a.flush('failed', { status: 500, statusText: 'Failed' });
    expect(service.commitDetail()?.commit).toEqual(commits[1]);
    expect(service.commitFilesState()).toBe('loading');
    b.flush({ files: [] });
    expect(service.commitFilesState()).toBe('loaded');
  });

  it('invalidates an in-flight read when a chain changes while hidden', () => {
    service.selectChainCommit(commits[0].sha);
    const stale = perSha(0);
    service.setJob({ ...job, commits: [commits[1]] }, false);
    stale.flush({ files: files('stale') });
    expect(service.commitFiles()).toEqual([]);
    expect(service.selectedDiffPath()).toBeNull();
    service.setJob({ ...job, commits: [commits[1]] }, true);
    drainMetadata();
    http.expectOne(r => r.url.endsWith('/commit')).flush({ commit: commits[1], files: [] });
    expect(service.commitDetail()?.commit).toEqual(commits[1]);
    expect(service.commitFilesState()).toBe('loaded');
  });

  it('invalidates pending file reads on disposal', () => {
    service.selectChainCommit(commits[0].sha);
    const pending = perSha(0);
    service.ngOnDestroy();
    pending.flush({ files: files('stale') });
    expect(service.commitFiles()).toEqual([]);
    expect(service.selectedDiffPath()).toBeNull();
  });
});

describe('GitPaneService superseded delivery rounds', () => {
  let service: GitPaneService;
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        GitPaneService,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    service = TestBed.inject(GitPaneService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('uses only current commits for the default and aggregate decision', () => {
    const oldRound = {
      sha: 'a'.repeat(40),
      shortSha: 'aaaaaaa',
      message: 'wip(runner): salvage before teardown - outcome Done',
      filesChanged: 1,
      files: ['src/a.ts'],
      at: '2026-08-09T10:00:00Z',
      runAttemptId: 'round-1',
      supersededBySha: 'b'.repeat(40),
    };
    const replacement = {
      sha: 'b'.repeat(40),
      shortSha: 'bbbbbbb',
      message: 'feat(AGT-2533): replacement',
      filesChanged: 1,
      files: ['src/a.ts'],
      at: '2026-08-09T11:00:00Z',
      runAttemptId: 'round-2',
    };
    service.setJob({
      id: 'job-x',
      watchPath: '/wp',
      commits: [oldRound, replacement],
      commit: replacement,
    } as unknown as TaskInfo);

    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/commit')).flush({ commit: replacement, files: [] });
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush({ entries: [] });

    expect(service.commitChain()).toHaveLength(2);
    expect(service.activeCommitChain()).toEqual([replacement]);
    expect(service.isAggregate()).toBe(false);
    expect(service.selectedCommitSha()).toBe(replacement.sha);
  });
});

/**
 * md/html preview (AGT-2008): the git-pane fetches a file's full text so it
 * can render a formatted preview instead of the diff. The source ref follows
 * the current view — the working tree in worktree mode, the newest task commit
 * in the aggregated commit view — and results are cached so toggling
 * Diff <-> Preview is instant.
 */
describe('GitPaneService.loadPreview', () => {
  let service: GitPaneService;
  let http: HttpTestingController;

  async function boot(job: TaskInfo, drain: () => void) {
    await TestBed.configureTestingModule({
      providers: [
        GitPaneService,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    service = TestBed.inject(GitPaneService);
    http = TestBed.inject(HttpTestingController);
    service.setJob(job);
    drain();
  }

  afterEach(() => http.verify());

  it('reads the working-tree file in worktree mode and caches it', async () => {
    const job = { id: 'job-x', watchPath: '/wp' } as unknown as TaskInfo;
    await boot(job, () => {
      http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
      http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush({ entries: [] });
    });

    service.selectDiffPath('docs/start/README.md');
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/git/diff') && r.params.get('path') === 'docs/start/README.md')
      .flush(utf8Buffer('DIFF'));

    service.loadPreview('docs/start/README.md');
    const fileReq = http.expectOne(
      (r) => r.url.endsWith('/api/tasks/job-x/git/file') && r.params.get('path') === 'docs/start/README.md',
    );
    fileReq.flush({ content: '# Hello', isBinary: false });
    expect(service.previewContent()).toBe('# Hello');
    expect(service.previewIsBinary()).toBe(false);
    expect(service.previewLoading()).toBe(false);

    // Second call is served from cache — no new round-trip.
    service.loadPreview('docs/start/README.md');
    http.expectNone((r) => r.url.endsWith('/api/tasks/job-x/git/file'));
    expect(service.previewContent()).toBe('# Hello');
  });

  it('flags a binary blob and shows no text', async () => {
    const job = { id: 'job-x', watchPath: '/wp' } as unknown as TaskInfo;
    await boot(job, () => {
      http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
      http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush({ entries: [] });
    });

    service.selectDiffPath('logo.html');
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/git/diff') && r.params.get('path') === 'logo.html')
      .flush(utf8Buffer('DIFF'));

    service.loadPreview('logo.html');
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/git/file')).flush({ content: '', isBinary: true });
    expect(service.previewIsBinary()).toBe(true);
    expect(service.previewContent()).toBe('');
  });

  it('previews the newest task commit in the aggregated multi-commit view', async () => {
    const older = { sha: 'a'.repeat(40), shortSha: 'aaaaaaa', message: 'first', filesChanged: 1, files: ['README.md'], at: '2026-06-01T10:00:00Z' };
    const newer = { sha: 'b'.repeat(40), shortSha: 'bbbbbbb', message: 'second', filesChanged: 1, files: ['README.md'], at: '2026-06-02T10:00:00Z' };
    const job = { id: 'job-x', watchPath: '/wp', commits: [older, newer] } as unknown as TaskInfo;
    await boot(job, () => {
      http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
      // Multi-commit -> aggregate file list + code-review listing.
      http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/commits/files')).flush({ files: [{ status: 'M', path: 'README.md', added: 1, removed: 0 }] });
      http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush({ entries: [] });
      // Aggregate default-selects README.md, firing its aggregate diff.
      http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/commits/diff') && r.params.get('path') === 'README.md').flush({ diff: 'DIFF' });
    });

    service.loadPreview('README.md');
    // Preview must hit the NEWEST commit's blob, not the oldest.
    const req = http.expectOne(
      (r) => r.url.endsWith(`/api/tasks/job-x/commits/${'b'.repeat(40)}/file`) && r.params.get('path') === 'README.md',
    );
    req.flush({ content: '# newest', isBinary: false });
    expect(service.previewContent()).toBe('# newest');
  });
});

function utf8Buffer(value: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(value);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}

/**
 * AGT-3096: the task detail calls `setJob` from an effect. A same-job refresh
 * of a commit-less core-first card builds a fresh empty chain each call; if
 * `setJob` let the effect track `commitChain`, that write re-ran the effect
 * forever and froze the browser once the documents painted.
 */
describe('GitPaneService.setJob inside an effect', () => {
  it('does not make the calling effect depend on the commit chain', async () => {
    await TestBed.configureTestingModule({
      providers: [
        GitPaneService,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const service = TestBed.inject(GitPaneService);
    const http = TestBed.inject(HttpTestingController);
    const detail = signal({ id: 'job-x', watchPath: '/wp', commit: null } as unknown as TaskInfo);
    let runs = 0;
    TestBed.runInInjectionContext(() => effect(() => {
      runs++;
      service.setJob(detail());
    }));

    TestBed.tick();
    detail.set({ ...detail() });
    TestBed.tick();

    expect(runs).toBe(2);
    http.match(() => true).forEach(request => request.flush(null));
  });
});
