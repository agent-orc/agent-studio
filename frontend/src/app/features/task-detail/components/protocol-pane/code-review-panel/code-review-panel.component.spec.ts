import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { CodeReviewPanelComponent } from './code-review-panel.component';
import { CodeReviewActivityStore } from '../../../../../services/code-review-activity.store';
import type { TaskInfo } from '../../../../../models/task.model';
import { LARGE_DIFF_LINE_THRESHOLD } from '../../../../../utils/large-diff-gate';
import { DossierReferenceHydratorService } from '../../../../../services/dossier-reference-hydrator.service';
import {
  DOSSIER_FIXTURE_PATH,
  dossierChipsIn,
  provideDossierCatalogueStub,
} from '../../../../../../testing/dossier-references';

/**
 * Behaviour spec for the user-triggered code-review panel. Pins the
 * load-bearing flows the user can observe:
 *
 * <ol>
 *   <li>On init, the panel pulls the existing list endpoint.</li>
 *   <li>An empty list shows the empty-state hint.</li>
 *   <li>A populated list renders one row per entry, with the verdict.</li>
 *   <li>Run posts the chosen model, surfaces the running indicator, and
 *       refreshes the list when the call returns.</li>
 *   <li>The picker seeds from the configured default, or from a remembered
 *       last-used pair when one exists.</li>
 *   <li>A run registers in the shared activity store so the kanban card can
 *       show a progress badge, then clears it when the call resolves.</li>
 * </ol>
 *
 * <p>The HTTP layer is stubbed via Angular's testing controller so the
 * spec runs offline. Required-input seeding mirrors the smoke specs in
 * this folder.</p>
 */
describe('CodeReviewPanelComponent', () => {
  const LAST_AGENT_KEY = 'atp.codeReview.lastAgent';

  // Each test starts with no remembered pair, so the panel exercises the
  // "no last-used -> fetch configured default" path unless a test seeds it.
  beforeEach(() => {
    try {
      localStorage.removeItem(LAST_AGENT_KEY);
    } catch {
      // No storage in this environment; the component tolerates that.
    }
  });

  function setup() {
    return TestBed.configureTestingModule({
      imports: [CodeReviewPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideDossierCatalogueStub(),
      ],
    }).compileComponents();
  }

  function seedJob(): TaskInfo {
    return {
      id: 'demo-job',
      taskKey: 'p::demo-job',
      title: 'Demo job',
      state: '3-progress',
      order: 1,
      agent: 'claude',
      cliType: 'claude',
      sessionName: '',
      ownerClientId: 'local-default',
      watchPath: 'C:/projects/foo',
      projectName: 'Foo',
      folderPath: 'C:/projects/foo/3-progress/demo-job',
      createdAt: '2026-05-01T00:00:00Z',
      sessionChain: [],
    } as unknown as TaskInfo;
  }

  it('pulls the listing on init and renders an empty-state when no MDs exist', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    // First visit with nothing remembered: the panel fetches the configured
    // default before listing existing reviews.
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    const req = httpCtrl.expectOne((r) => r.url.includes('/api/tasks/demo-job/code-review/list'));
    expect(req.request.method).toBe('GET');
    req.flush({ entries: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="code-review-empty"]')?.textContent).toMatch(/No reviews yet/);
    httpCtrl.verify();
  });

  it('renders one row per listed review with the verdict label', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/api/tasks/demo-job/code-review/list')).flush({
      entries: [
        {
          fileName: 'code-review-2026-05-14T12-00-00Z.md',
          verdict: 'pass',
          summary: 'Looks fine.',
          model: 'claude-haiku-4-5',
          cliType: 'claude',
          commit: '0aa4c5d',
          runAt: '2026-05-14T12:00:00Z',
          inputTokens: 100,
          outputTokens: 25,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          totalTokens: 125,
          estimatedApiCostUsd: 0.0042,
          priceKnown: true,
          generation: {
            file: 'code-review-2026-05-14T12-00-00Z.md',
            kind: 'code-review',
            model: 'claude-haiku-4-5',
            cli: 'claude',
            tokensIn: 100,
            tokensOut: 25,
            tokensTotal: 125,
            startedAt: '2026-05-14T11:59:58Z',
            endedAt: '2026-05-14T12:00:00Z',
            durationMs: 2000,
            stepId: 'code-review-step',
          },
        },
        {
          fileName: 'code-review-2026-05-13T18-30-00Z.md',
          verdict: 'concerns',
          summary: 'Helper duplicated.',
          model: 'claude-sonnet-4-6',
          cliType: 'claude',
          commit: '7892fe6',
          runAt: '2026-05-13T18:30:00Z',
        },
      ],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const list = root.querySelector('[data-testid="code-review-list"]');
    expect(list).toBeTruthy();
    expect(list?.querySelectorAll('.cr-row').length).toBe(2);
    const verdicts = Array.from(list?.querySelectorAll('.cr-verdict') ?? []).map((el) => el.textContent?.trim());
    expect(verdicts).toEqual(['pass', 'concerns']);
    const provenance = root.querySelector('[data-testid="code-review-provenance"]');
    expect(provenance?.textContent).toContain('claude / claude-haiku-4-5');
    expect(provenance?.textContent).toContain('125 tokens');
    const usage = root.querySelector('[data-testid="code-review-token-usage"]');
    expect(usage?.textContent).toContain('100 in / 25 out (125) tokens');
    const entry = fixture.componentInstance.entries()[0];
    expect(fixture.componentInstance.tokenTooltip(entry)).toContain('Estimated cost: $0.0042');
    expect(fixture.componentInstance.tokenTooltip(entry)).toContain('historical list prices');
    expect(fixture.componentInstance.tokenTooltip(entry)).toContain('0 cache read + 0 cache write');
    httpCtrl.verify();
  });

  it('renders the council reaction, per-finding rulings, and linked next round on the review row', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', { ...seedJob(), key: 'AGT-2108' });
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl.expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'codex', model: 'gpt-5.5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [{
      fileName: 'code-review-grade-2026-07-23.md', verdict: 'pass', grade: 'B',
      summary: 'Concrete gaps remain.', model: 'gpt-5.5', cliType: 'codex',
      runAt: '2026-07-23T10:00:00Z',
      councilReaction: {
        createdAt: '2026-07-23T10:00:01Z', reviewFileName: 'code-review-grade-2026-07-23.md',
        grade: 'B', disposition: 'Reissue', summary: 'Fix 2 review finding(s) in the next round.',
        startsNewRound: true, targetJobId: 'demo-job', targetRunAttempt: 2,
        assessments: [
          { finding: 'Dark-theme colors are incorrect; provide both-theme screenshots.', action: 'FixNextRound', reason: 'Concrete review deficiency.' },
          { finding: 'Upload rejection lacks focused test evidence.', action: 'FixNextRound', reason: 'Concrete review deficiency.' },
        ],
      },
    }] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const reaction = root.querySelector('[data-testid="code-review-council-reaction"]');
    expect(reaction?.textContent).toContain('Orchestrator reaction');
    expect(reaction?.textContent).toContain('Dark-theme colors are incorrect');
    expect(reaction?.querySelectorAll('.council-reaction__findings li')).toHaveLength(2);
    const round = reaction?.querySelector('[data-testid="code-review-council-round-link"]') as HTMLAnchorElement;
    expect(round.textContent).toContain('Open round 2');
    expect(round.getAttribute('href')).toContain('#/tasks/AGT-2108');
    httpCtrl.verify();
  });

  it('shows the last grade with date when the newest delivery has no fresh grade', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl.expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({
      entries: [
        {
          fileName: 'code-review-2026-07-11.md', verdict: 'pass', summary: 'Latest delivery review.',
          model: 'claude-haiku-4-5', cliType: 'claude', runAt: '2026-07-11T12:00:00Z',
        },
        {
          fileName: 'code-review-grade-2026-07-09.md', verdict: 'pass', grade: 'B', summary: 'Prior grade.',
          model: 'claude-opus-4-8', cliType: 'claude', runAt: '2026-07-09T19:22:02Z',
        },
      ],
    });
    fixture.detectChanges();

    const fallback = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="code-review-older-grade"]');
    expect(fallback?.textContent).toContain('Grade B');
    expect(fallback?.textContent).toContain('older delivery');
    expect(fallback?.textContent).toContain('2026');
    httpCtrl.verify();
  });

  it('marks the only available grade as older when the delivered commit is newer', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    const job = seedJob();
    job.commits = [{
      sha: 'new-delivery', shortSha: 'new-del', message: 'newer delivery',
      filesChanged: 1, files: ['new.ts'], at: '2026-07-11T12:00:00Z',
    }];
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl.expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({
      entries: [{
        fileName: 'code-review-grade-2026-07-09.md', verdict: 'pass', grade: 'B', summary: 'Prior grade.',
        model: 'claude-opus-4-8', cliType: 'claude', runAt: '2026-07-09T19:22:02Z',
      }],
    });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="code-review-older-grade"]')?.textContent).toContain('older delivery');
    httpCtrl.verify();
  });

  it('does not call a current grade an older delivery because of unrelated task activity', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    const job = seedJob();
    job.lastActivity = '2026-07-11T12:00:00Z';
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl.expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({
      entries: [{
        fileName: 'code-review-grade-2026-07-09.md', verdict: 'pass', grade: 'B', summary: 'Current grade.',
        model: 'claude-opus-4-8', cliType: 'claude', runAt: '2026-07-09T19:22:02Z',
      }],
    });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="code-review-older-grade"]')).toBeNull();
    httpCtrl.verify();
  });

  it('posts the chosen model when Run is clicked, shows the running indicator, then refreshes the list', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    // The unified selector is a chip + popover, not a <select>. Drive the
    // signal directly to exercise the "operator picked a different model"
    // path without re-opening the picker in a unit test.
    fixture.componentInstance.onAgentCommit({ cliType: 'claude', model: 'claude-haiku-4-5-20251001', thinkingLevel: null });
    // Committing through the picker persists the pair for the next visit.
    expect(JSON.parse(localStorage.getItem(LAST_AGENT_KEY) ?? '{}')).toEqual({
      cliType: 'claude',
      model: 'claude-haiku-4-5-20251001',
      thinkingLevel: null,
    });

    const button = root.querySelector('[data-testid="code-review-run"]') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-running"]')).toBeTruthy();

    const post = httpCtrl.expectOne(
      (r) => r.method === 'POST' && r.url.includes('/api/tasks/demo-job/code-review')
    );
    expect(post.request.body).toEqual({ cliType: 'claude', model: 'claude-haiku-4-5-20251001', mode: 'verdict' });
    post.flush({
      fileName: 'code-review-2026-05-14T13-00-00Z.md',
      verdict: 'pass',
      summary: 'ok',
      model: 'claude-haiku-4-5-20251001',
      cliType: 'claude',
      commit: 'abcdef0',
      durationMs: 1234,
      startedAt: '2026-05-14T13:00:00Z',
    });
    fixture.detectChanges();

    // After the post resolves, the panel re-pulls the list.
    httpCtrl.expectOne((r) => r.method === 'GET' && r.url.includes('/code-review/list')).flush({
      entries: [
        {
          fileName: 'code-review-2026-05-14T13-00-00Z.md',
          verdict: 'pass',
          summary: 'ok',
          model: 'claude-haiku-4-5-20251001',
          cliType: 'claude',
          commit: 'abcdef0',
          runAt: '2026-05-14T13:00:00Z',
        },
      ],
    });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-running"]')).toBeNull();
    expect(root.querySelectorAll('.cr-row').length).toBe(1);
    // The pair the backend ran with is remembered for the next visit.
    expect(JSON.parse(localStorage.getItem(LAST_AGENT_KEY) ?? '{}')).toEqual({
      cliType: 'claude',
      model: 'claude-haiku-4-5-20251001',
      thinkingLevel: null,
    });
    httpCtrl.verify();
  });

  it('retro-grades a finished card on demand and renders the versioned grade result', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', { ...seedJob(), state: '6-completed' });
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl.expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-opus-4-8' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('[data-testid="code-review-grade-run"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-grade-running"]')).toBeTruthy();
    const post = httpCtrl.expectOne((r) => r.method === 'POST' && r.url.includes('/code-review'));
    expect(post.request.body.mode).toBe('grade');
    post.flush({
      fileName: 'code-review-grade-2026-07-12T01-00-00Z.md',
      verdict: 'pass',
      grade: 'A',
      summary: 'Complete and well tested.',
      model: 'claude-opus-4-8',
      cliType: 'claude',
      commit: 'base..task/demo-job',
      durationMs: 40,
      startedAt: '2026-07-12T01:00:00Z',
    });
    httpCtrl.expectOne((r) => r.method === 'GET' && r.url.includes('/code-review/list')).flush({ entries: [{
      fileName: 'code-review-grade-2026-07-12T01-00-00Z.md',
      verdict: 'pass', grade: 'A', summary: 'Complete and well tested.',
      model: 'claude-opus-4-8', cliType: 'claude', commit: 'base..task/demo-job',
      runAt: '2026-07-12T01:00:00Z',
    }] });
    fixture.detectChanges();

    expect(root.querySelector('.cr-verdict')?.textContent).toContain('Grade A');
    httpCtrl.verify();
  });

  it('renders an expanded review body as formatted markdown, not a raw blob', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({
      entries: [
        {
          fileName: 'code-review-2026-05-14T12-00-00Z.md',
          verdict: 'concerns',
          summary: 'Helper duplicated.',
          model: 'claude-haiku-4-5',
          cliType: 'claude',
          commit: '0aa4c5d',
          runAt: '2026-05-14T12:00:00Z',
        },
      ],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('.cr-row-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();

    const read = httpCtrl.expectOne((r) => r.url.includes('/code-review/code-review-2026-05-14T12-00-00Z.md'));
    read.flush({
      fileName: 'code-review-2026-05-14T12-00-00Z.md',
      content: [
        '---',
        'type: code-review-step',
        'verdict: concerns',
        'summary: Helper duplicated.',
        '---',
        '',
        '# Code Review Step',
        '',
        '**Verdict:** concerns',
        '',
        '## Reviewer reply',
        '',
        '```',
        '### Findings',
        '',
        '- duplicated `helper`',
        '- second point',
        '',
        '[[ASPECT_VERDICT: status=concerns; summary=Helper duplicated.]]',
        '```',
      ].join('\n'),
    });
    fixture.detectChanges();

    const body = root.querySelector('[data-testid="code-review-body"]');
    expect(body).toBeTruthy();
    expect(body?.querySelector('.cr-body-header')?.textContent).toContain('Helper duplicated.');
    expect(body?.querySelector('.cr-body-chip--concerns')?.textContent?.trim()).toBe('concerns');
    // No raw <pre> dark blob; the canonical markdown surface rendered structure.
    expect(body?.querySelector('pre')).toBeNull();
    expect(body?.querySelector('cac-markdown')).toBeTruthy();
    expect(body?.querySelector('h1')?.textContent).toMatch(/Code Review Step/);
    expect(body?.querySelector('h3')?.textContent).toMatch(/Findings/);
    expect(body?.querySelectorAll('li').length).toBe(2);
    expect(body?.querySelector('code')?.textContent).toBe('helper');
    expect(body?.textContent).not.toContain('ASPECT_VERDICT');
    httpCtrl.verify();
  });

  it('shows a placeholder for a large review body until it is revealed', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({
      entries: [
        {
          fileName: 'code-review-large.md',
          verdict: 'concerns',
          summary: 'Generated diff is large.',
          model: 'claude-haiku-4-5',
          cliType: 'claude',
          commit: '0aa4c5d',
          runAt: '2026-05-14T12:00:00Z',
        },
      ],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('.cr-row-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();

    const largeBody = [
      '# Code Review Step',
      '',
      ...Array.from({ length: LARGE_DIFF_LINE_THRESHOLD }, (_, i) => `- generated finding ${i}`),
    ].join('\n');
    httpCtrl.expectOne((r) => r.url.includes('/code-review/code-review-large.md')).flush({
      fileName: 'code-review-large.md',
      content: largeBody,
    });
    fixture.detectChanges();

    const gated = root.querySelector('[data-testid="code-review-body-gated"]');
    expect(gated?.textContent).toContain('code-review-large.md');
    expect(gated?.textContent).toContain('Show review body');
    expect(root.querySelector('cac-markdown')).toBeNull();

    root.querySelector<HTMLButtonElement>('[data-testid="code-review-body-show"]')?.click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-body-gated"]')).toBeNull();
    expect(root.querySelector('cac-markdown')).toBeTruthy();
    httpCtrl.verify();
  });

  it('surfaces an error message when the run POST fails', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('[data-testid="code-review-run"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const post = httpCtrl.expectOne((r) => r.method === 'POST');
    post.flush({ error: 'CLI exploded' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-error"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="code-review-running"]')).toBeNull();
    httpCtrl.verify();
  });

  it('seeds the picker from the configured backend default when nothing is remembered', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'codex', model: 'gpt-5-codex' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    expect(fixture.componentInstance.selectedCli()).toBe('codex');
    expect(fixture.componentInstance.selectedModel()).toBe('gpt-5-codex');
    httpCtrl.verify();
  });

  it('seeds from the remembered last-used pair and skips the configured-default fetch', async () => {
    localStorage.setItem(
      LAST_AGENT_KEY,
      JSON.stringify({ cliType: 'codex', model: 'gpt-5-codex' }),
    );
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    // A remembered pair short-circuits the defaults round-trip entirely.
    httpCtrl.expectNone((r) => r.url.includes('/tasks/code-review/defaults'));
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    expect(fixture.componentInstance.selectedCli()).toBe('codex');
    expect(fixture.componentInstance.selectedModel()).toBe('gpt-5-codex');
    httpCtrl.verify();
  });

  it('marks the shared activity store while a run is in flight and clears it on completion', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    const store = TestBed.inject(CodeReviewActivityStore);
    const key = CodeReviewActivityStore.key('C:/projects/foo', 'demo-job');
    expect(store.isRunning(key)).toBe(false);

    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('[data-testid="code-review-run"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    // While the synchronous POST is outstanding, the kanban card badge is lit.
    expect(store.isRunning(key)).toBe(true);

    const post = httpCtrl.expectOne((r) => r.method === 'POST');
    post.flush({
      fileName: 'code-review-2026-05-14T13-00-00Z.md',
      verdict: 'pass',
      summary: 'ok',
      model: 'claude-haiku-4-5',
      cliType: 'claude',
      commit: 'abcdef0',
      durationMs: 10,
      startedAt: '2026-05-14T13:00:00Z',
    });
    fixture.detectChanges();

    httpCtrl.expectOne((r) => r.method === 'GET' && r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    expect(store.isRunning(key)).toBe(false);
    httpCtrl.verify();
  });

  // AGT-2812: review documents are a document surface too. A Dossier path in a
  // finding resolves to the Dossier view instead of a raw file link.
  it('renders a Dossier path in a review body as a chip', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl.expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-haiku-4-5' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({
      entries: [{
        fileName: 'code-review-dossier.md', verdict: 'concerns',
        summary: 'Contradicts the Dossier.', model: 'claude-haiku-4-5',
        cliType: 'claude', commit: '0aa4c5d', runAt: '2026-05-14T12:00:00Z',
      }],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('.cr-row-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();
    httpCtrl.expectOne((r) => r.url.includes('/code-review/code-review-dossier.md')).flush({
      fileName: 'code-review-dossier.md',
      content: `# Code Review Step\n\nThis contradicts \`${DOSSIER_FIXTURE_PATH}\`.\n`,
    });
    fixture.detectChanges();
    TestBed.inject(DossierReferenceHydratorService).refresh();

    const chips = dossierChipsIn(root);
    expect(chips).toHaveLength(1);
    expect(chips[0].textContent).toContain('AGT-W54');
    httpCtrl.verify();
  });
});
