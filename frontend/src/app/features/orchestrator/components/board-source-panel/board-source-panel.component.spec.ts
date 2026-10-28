import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { BoardSourceStatus } from '../../../../services/board-source.service';
import { BoardSourcePanelComponent } from './board-source-panel.component';

const statusUrl = '/api/admin/board-source';
const explainUrl = '/api/admin/board-source/differences/explain';

function status(overrides: Partial<BoardSourceStatus> = {}): BoardSourceStatus {
  return {
    source: 'task-server-shadow',
    rejectedSetting: null,
    mirroring: true,
    sync: {
      syncedGeneration: 42, syncedAt: '2026-10-27T09:00:00Z', lastError: null, lastErrorAt: null,
      missingProjects: [], rejectedCards: 0,
    },
    comparisonRequests: 120,
    comparisons: 118,
    skippedComparisons: 2,
    lastComparisonAt: '2026-10-27T09:00:00Z',
    lastComparedGeneration: 42,
    unexplainedDifferences: 1,
    explainedDifferences: 0,
    differences: [{
      fingerprint: 'Agent Studio|AGT-1|title', project: 'Agent Studio', taskKey: 'AGT-1', field: 'title',
      workspaceValue: 'Mine', taskServerValue: 'Theirs', firstSeenAt: '2026-10-27T08:00:00Z',
      lastSeenAt: '2026-10-27T09:00:00Z', occurrences: 3, present: true,
      explainedAt: null, explanation: null, explainedBy: null,
    }],
    readiness: { state: 'clean-window-open', ready: false, cleanSince: '2026-10-25T09:00:00Z', cleanDays: 2, requiredDays: 7 },
    boardRead: { sampleCount: 40, p95Ms: 120, spawnsPerRequest: 0, targetP95Ms: 200, targetSpawnsPerRequest: 0, met: true },
    ...overrides,
  };
}

async function settle(): Promise<void> {
  for (let index = 0; index < 5; index++) await new Promise(resolve => setTimeout(resolve, 0));
}

describe('BoardSourcePanelComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [BoardSourcePanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render(view: BoardSourceStatus) {
    const fixture = TestBed.createComponent(BoardSourcePanelComponent);
    fixture.detectChanges();
    http.expectOne(statusUrl).flush(view);
    await settle();
    fixture.detectChanges();
    return fixture;
  }

  function text(root: HTMLElement, testId: string): string {
    return root.querySelector(`[data-testid="${testId}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  }

  it('shows the source, mirror, comparisons, cutover window and board read against their targets', async () => {
    const root: HTMLElement = (await render(status())).nativeElement;

    expect(text(root, 'board-source-mode')).toBe('task-server-shadow');
    expect(text(root, 'board-source-sync')).toContain('generation 42');
    expect(text(root, 'board-source-comparisons')).toBe('118 of 120 reads, 2 skipped');
    expect(text(root, 'board-source-differences-count')).toBe('1 unexplained, 0 explained');
    expect(text(root, 'board-source-readiness')).toContain('Clean window open');
    expect(text(root, 'board-source-readiness')).toContain('2.0 of 7 days clean');
    expect(text(root, 'board-source-board-read')).toContain('p95 120 ms (target under 200 ms)');
    expect(text(root, 'board-source-board-read')).toContain('0 Git spawns per read (target 0)');
    const row = root.querySelector('[data-fingerprint="Agent Studio|AGT-1|title"]')!;
    expect(row.textContent).toContain('Mine');
    expect(row.textContent).toContain('Theirs');
  });

  it('warns about a rejected setting and projects missing on the Task Server', async () => {
    const root: HTMLElement = (await render(status({
      source: 'workspace',
      rejectedSetting: 'taskserver',
      sync: { ...status().sync, missingProjects: ['Other'] },
      differences: [],
    }))).nativeElement;

    expect(text(root, 'board-source-rejected')).toContain('"taskserver" is not a known value');
    expect(text(root, 'board-source-missing')).toBe('Not on the Task Server: Other');
    expect(text(root, 'board-source-differences')).toContain('No difference recorded.');
  });

  it('records an explanation for a difference and shows who explained it', async () => {
    const fixture = await render(status());
    const root: HTMLElement = fixture.nativeElement;

    (root.querySelector('[data-testid="board-source-explain"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const input = root.querySelector('[data-testid="board-source-explanation"]') as HTMLInputElement;
    input.value = 'Title trimmed on import';
    input.dispatchEvent(new Event('input'));
    (root.querySelector('[data-testid="board-source-explain-save"]') as HTMLButtonElement).click();

    const request = http.expectOne(explainUrl);
    expect(request.request.body).toEqual({ fingerprint: 'Agent Studio|AGT-1|title', explanation: 'Title trimmed on import' });
    const explained = status();
    request.flush({
      ...explained,
      unexplainedDifferences: 0,
      explainedDifferences: 1,
      differences: [{ ...explained.differences[0], explainedAt: '2026-10-27T09:05:00Z', explanation: 'Title trimmed on import', explainedBy: 'local-default' }],
    });
    await settle();
    fixture.detectChanges();

    expect(text(root, 'board-source-differences-count')).toBe('0 unexplained, 1 explained');
    expect(root.querySelector('[data-testid="board-source-explain"]')).toBeNull();
    expect(text(root, 'board-source-differences')).toContain('explained by local-default');
  });
});
