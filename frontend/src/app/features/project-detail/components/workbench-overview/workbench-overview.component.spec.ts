import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { TaskReferenceStatus } from '../../../../components/task-reference-microcard/task-reference-microcard';
import type {
  WorkbenchOverview,
  WorkbenchOverviewItem,
  WorkbenchStatus,
} from '../../../../models/project-docs.model';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { WorkbenchOverviewComponent } from './workbench-overview.component';

function item(
  id: string,
  status: WorkbenchStatus,
  openDecisionCount = 0,
  pattern?: WorkbenchOverviewItem['workbench']['pattern'],
): WorkbenchOverviewItem {
  return {
    projectName: id.startsWith('other') ? 'Other' : 'Demo',
    workbench: {
      id,
      title: id,
      summary: `${id} summary`,
      status,
      phase: 'testing',
      updatedAtUtc: '2026-08-09T10:00:00Z',
      entryPath: `docs/${id}/index.html`,
      valid: true,
      error: null,
      sourceTaskKeys: [],
      openDecisionCount,
      pattern,
    },
  };
}

function taskStatus(key: string, lane: string, taskKey = `Demo::${key}`): TaskReferenceStatus {
  return {
    key,
    exists: true,
    taskKey,
    title: `${key} implementation`,
    lane,
    projectId: 'PROJ-001',
    projectName: 'Demo',
    projectColor: null,
    merge: null,
    reviewGrade: null,
  };
}

function overview(items: WorkbenchOverviewItem[], projectName: string | null = null): WorkbenchOverview {
  return {
    projectName,
    count: items.length,
    currentCount: items.filter(entry => ['active', 'decision-pending', 'decided'].includes(entry.workbench.status)).length,
    historyCount: items.filter(entry => ['archived', 'documented'].includes(entry.workbench.status)).length,
    items,
  };
}

describe('WorkbenchOverviewComponent', () => {
  beforeEach(() => {
    sessionStorage.clear();
    for (const key of Object.keys(localStorage)) {
      if (key.startsWith('dossier-overview:')) localStorage.removeItem(key);
    }
    history.replaceState(null, '', '/#/workbenches');
  });

  afterEach(() => {
    vi.useRealTimers();
    document.querySelectorAll('.app-tooltip-overlay').forEach(element => element.remove());
  });

  it('renders calm lifecycle rows and hydrates every current Dossier reference in one stable batch', async () => {
    vi.useFakeTimers();
    const openTaskKey = vi.fn(() => true);
    await TestBed.configureTestingModule({
      imports: [WorkbenchOverviewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskReferenceNavigationService, useValue: { openTaskKey } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchOverviewComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const pending = item('pending', 'decision-pending', 3, 'ui');
    pending.workbench.documentation = {
      eligible: false,
      totalCount: 3,
      terminalCount: 0,
      openCount: 3,
      missingCount: 1,
      references: [
        { key: 'AGT-2', exists: true, terminal: false, lane: '2-ready' },
        { key: 'AGT-1', exists: true, terminal: false, lane: '3-progress' },
        { key: 'AGT-404', exists: false, terminal: false, lane: null },
      ],
    };
    const active = item('active', 'active');
    active.workbench.relatedTaskKeys = ['AGT-1'];
    const tracking = item('tracking', 'decided');
    tracking.workbench.relatedTaskKeys = ['AGT-3'];
    tracking.workbench.documentation = {
      eligible: false,
      totalCount: 1,
      terminalCount: 0,
      openCount: 1,
      missingCount: 0,
      references: [{ key: 'AGT-3', exists: true, terminal: false, lane: '4-auto-review' }],
    };
    http.expectOne('/api/workbenches').flush(overview([
      pending,
      tracking,
      active,
      item('discarded', 'archived'),
      item('documented', 'documented'),
    ]));
    fixture.detectChanges();

    const referenceRequest = http.expectOne('/api/tasks/reference-status');
    expect(referenceRequest.request.body).toEqual({ keys: ['AGT-2', 'AGT-1', 'AGT-404', 'AGT-3'] });
    expect(fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-task-Demo-pending-AGT-1"] a',
    )).toBeNull();
    referenceRequest.flush({ items: [
      taskStatus('AGT-1', '3-progress', 'Demo::active-card'),
      taskStatus('AGT-2', '2-ready', 'Demo::ready-card'),
      taskStatus('AGT-3', '4-auto-review', 'Demo::review-card'),
    ] });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('h1')?.textContent).toContain('Dossiers');
    expect(fixture.nativeElement.querySelectorAll('[data-testid^="workbench-overview-sort-"]').length).toBe(5);
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-current-count"]')?.textContent)
      .toContain('3 current');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-history-count"]')?.textContent)
      .toContain('2 history');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-active-count"]')?.textContent)
      .toContain('2');
    expect([...fixture.nativeElement.querySelectorAll('[data-testid="workbench-overview-active-list"] > article')]
      .map((row: Element) => row.getAttribute('data-testid')))
      .toEqual(['workbench-overview-item-Demo-active', 'workbench-overview-item-Demo-tracking']);
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-history-section-count"]')?.textContent)
      .toContain('2');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-decision-pending"]')?.textContent)
      .toContain('3 open');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-pattern-Demo-pending"]')?.getAttribute('data-pattern'))
      .toBe('ui');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-pattern-Demo-active"]')?.getAttribute('data-pattern'))
      .toBe('concept');
    const decidedRow = fixture.nativeElement.querySelector('[data-testid="workbench-overview-item-Demo-tracking"]');
    expect(decidedRow?.textContent).toContain('Accepted / In progress');
    expect(decidedRow?.textContent).not.toContain('Decision pending');
    expect(decidedRow?.textContent).not.toContain('Tracking');

    const pendingCards = fixture.nativeElement.querySelectorAll(
      '[data-testid="workbench-overview-item-Demo-pending"] app-task-reference-microcard',
    );
    expect([...pendingCards].map((card: Element) => card.querySelector('[data-testid]')?.getAttribute('data-testid')))
      .toEqual([
        'workbench-overview-task-Demo-pending-AGT-2',
        'workbench-overview-task-Demo-pending-AGT-1',
        'workbench-overview-task-Demo-pending-AGT-404',
      ]);
    const activeCard = fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-task-Demo-pending-AGT-1"]',
    );
    expect(activeCard.querySelector('.task-ref__lane-dot')?.getAttribute('data-tone')).toBe('active');
    const activeCardLink = activeCard.querySelector('a') as HTMLAnchorElement;
    expect(activeCardLink.getAttribute('href')).toBe('#task:AGT-1');
    activeCardLink.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    vi.advanceTimersByTime(300);
    expect(document.querySelector(
      '[data-testid="workbench-overview-task-Demo-pending-AGT-1-tooltip"]',
    )?.textContent).toContain('In progress');
    activeCardLink.click();
    expect(openTaskKey).toHaveBeenCalledWith('Demo::active-card');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-task-Demo-pending-AGT-404"] a',
    )).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-list"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-list"]')).not.toBeNull();

    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-toggle"]') as HTMLButtonElement).click();
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-list"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-list"]')).toBeNull();
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-toggle"]') as HTMLButtonElement).click();
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-list"]')?.textContent)
      .toContain('discarded');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-list"]')?.textContent)
      .toContain('documented');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-list"]')?.textContent)
      .toContain('Documented');

    let opened: WorkbenchOverviewItem | null = null;
    fixture.componentInstance.openWorkbench.subscribe(value => opened = value);
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-full-Demo-pending"]') as HTMLButtonElement).click();
    expect(opened).toEqual(pending);

    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-open-Demo-pending"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-inline-Demo-pending"]'))
      .not.toBeNull();
    http.expectOne('/api/projects/Demo/workbenches/pending').flush({
      workbench: pending.workbench,
      html: '<section data-decision-id="route" data-decision-kind="single"><strong>Choose route</strong><span data-option-id="direct">Direct</span></section>',
      branch: 'develop',
      revision: 'abc123',
      workingTreeModified: false,
      fingerprint: 'fingerprint',
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-viewer"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-viewer-open-wiki"]')).toBeNull();
    http.verify();
  });

  it('toggles, persists, restores focus, and reopens a section when items newly appear', async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({
      imports: [WorkbenchOverviewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const http = TestBed.inject(HttpTestingController);
    const mount = () => {
      const fixture = TestBed.createComponent(WorkbenchOverviewComponent);
      fixture.componentRef.setInput('projectName', 'Demo');
      fixture.componentRef.setInput('projectId', 'PROJ-001');
      fixture.detectChanges();
      return fixture;
    };

    const fixture = mount();
    http.expectOne(request => request.url === '/api/workbenches' && request.params.get('project') === 'Demo')
      .flush(overview([item('pending', 'decision-pending', 1), item('active', 'active')], 'Demo'));
    fixture.detectChanges();

    const header = fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-decision-toggle"]',
    ) as HTMLButtonElement;
    expect(header.getAttribute('aria-expanded')).toBe('true');
    expect(header.getAttribute('aria-controls')).toBe('workbench-overview-decision-list');
    const rowAction = fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-full-Demo-pending"]',
    ) as HTMLButtonElement;
    rowAction.focus();
    header.click();
    fixture.detectChanges();

    expect(document.activeElement).toBe(header);
    expect(header.getAttribute('aria-expanded')).toBe('false');
    expect(fixture.nativeElement.querySelector('#workbench-overview-decision-list')).toBeNull();
    expect(JSON.parse(localStorage.getItem('dossier-overview:PROJ-001:needs-decision') ?? '{}'))
      .toEqual({ collapsed: true, hadItems: true });

    fixture.destroy();
    const reloaded = mount();
    http.expectOne(request => request.url === '/api/workbenches' && request.params.get('project') === 'Demo')
      .flush(overview([item('pending', 'decision-pending', 1), item('active', 'active')], 'Demo'));
    reloaded.detectChanges();
    expect(reloaded.nativeElement.querySelector(
      '[data-testid="workbench-overview-decision-toggle"]')?.getAttribute('aria-expanded')).toBe('false');

    const hub = TestBed.inject(JobsHubClient);
    hub.workbenchEvent.set({
      type: 'statusChanged', projectName: 'Demo', workbenchId: 'pending', workbench: null,
      previousStatus: 'decision-pending', occurredAtUtc: '2026-09-06T10:00:00Z',
    });
    reloaded.detectChanges();
    await vi.advanceTimersByTimeAsync(80);
    http.expectOne(request => request.url === '/api/workbenches' && request.params.get('project') === 'Demo')
      .flush(overview([item('active', 'active')], 'Demo'));
    reloaded.detectChanges();

    hub.workbenchEvent.set({
      type: 'created', projectName: 'Demo', workbenchId: 'fresh', workbench: null,
      previousStatus: null, occurredAtUtc: '2026-09-06T10:01:00Z',
    });
    reloaded.detectChanges();
    await vi.advanceTimersByTimeAsync(80);
    http.expectOne(request => request.url === '/api/workbenches' && request.params.get('project') === 'Demo')
      .flush(overview([item('fresh', 'decision-pending', 1), item('active', 'active')], 'Demo'));
    reloaded.detectChanges();

    expect(reloaded.nativeElement.querySelector(
      '[data-testid="workbench-overview-decision-toggle"]')?.getAttribute('aria-expanded')).toBe('true');
    expect(reloaded.nativeElement.querySelector('#workbench-overview-decision-list')?.textContent)
      .toContain('fresh');
    http.verify();
  });

  it('refreshes a project-scoped queue after a matching live event', async () => {
    vi.useFakeTimers();
    try {
      await TestBed.configureTestingModule({
        imports: [WorkbenchOverviewComponent],
        providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
      }).compileComponents();
      const fixture = TestBed.createComponent(WorkbenchOverviewComponent);
      fixture.componentRef.setInput('projectName', 'Demo');
      fixture.detectChanges();
      const http = TestBed.inject(HttpTestingController);
      http.expectOne(request => request.url === '/api/workbenches' && request.params.get('project') === 'Demo')
        .flush(overview([item('active', 'active')], 'Demo'));

      TestBed.inject(JobsHubClient).workbenchEvent.set({
        type: 'created',
        projectName: 'Demo',
        workbenchId: 'new-item',
        workbench: null,
        previousStatus: null,
        occurredAtUtc: new Date().toISOString(),
      });
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(80);
      http.expectOne(request => request.url === '/api/workbenches' && request.params.get('project') === 'Demo')
        .flush(overview([item('new-item', 'active'), item('active', 'active')], 'Demo'));
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('new-item');
      http.verify();
    } finally {
      vi.useRealTimers();
    }
  });
});
