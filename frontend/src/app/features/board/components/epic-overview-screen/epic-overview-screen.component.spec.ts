import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { EpicOverviewScreenComponent, type EpicOverviewScope } from './epic-overview-screen.component';
import type { EpicRollup } from '../../../../models/task.model';

function rollup(over: Partial<EpicRollup>): EpicRollup {
  return {
    id: 'E1', title: 'Epic one', projectName: 'Acme', watchPath: '/repo/acme',
    state: '0-backlog', subTaskTotal: 0, completed: 0, inProgress: 0, open: 0,
    byState: {}, subTasks: [], ...over,
  };
}

function mount(scope: EpicOverviewScope | null, epics: EpicRollup[]) {
  TestBed.configureTestingModule({
    imports: [EpicOverviewScreenComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  const fixture = TestBed.createComponent(EpicOverviewScreenComponent);
  fixture.componentRef.setInput('scopedProject', scope);
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  http.expectOne((r) => r.url.endsWith('/epics') && r.params.get('status') === 'active').flush(epics);
  http.expectOne((r) => r.url.endsWith('/epics/completed/count')).flush({ count: 0 });
  fixture.detectChanges();
  return { fixture, http };
}

function testid(fixture: { nativeElement: HTMLElement }, id: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
}

function testids(fixture: { nativeElement: HTMLElement }, id: string): HTMLElement[] {
  return [...fixture.nativeElement.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
}

describe('EpicOverviewScreenComponent', () => {
  it('invites creation when a scoped project has no epics', () => {
    const { fixture, http } = mount({ name: 'Acme', watchPath: '/repo/acme' }, []);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    expect(testid(host, 'epic-overview-empty')).toBeTruthy();
    expect(testid(host, 'epic-overview-create')).toBeTruthy();
    expect(testid(host, 'epic-overview-new')).toBeTruthy();
    http.verify();
  });

  it('does not offer creation in the unscoped cross-project view', () => {
    const { fixture, http } = mount(null, []);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    expect(testid(host, 'epic-overview-empty')).toBeTruthy();
    expect(testid(host, 'epic-overview-create')).toBeNull();
    expect(testid(host, 'epic-overview-new')).toBeNull();
    http.verify();
  });

  it('shows only the scoped project\'s epics', () => {
    const epics = [
      rollup({ id: 'E1', projectName: 'Acme' }),
      rollup({ id: 'E2', projectName: 'Other' }),
    ];
    const { fixture, http } = mount({ name: 'Acme', watchPath: '/repo/acme' }, epics);
    expect(fixture.componentInstance.visibleEpics().map((e) => e.id)).toEqual(['E1']);
    http.verify();
  });

  it('loads completed epics only after its collapsed count header is opened', () => {
    const { fixture, http } = mount(null, [rollup({ id: 'active', subTaskTotal: 2, completed: 1 })]);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    expect(fixture.componentInstance.activeEpics().map((e) => e.id)).toEqual(['active']);
    expect(testid(host, 'epic-overview-section-completed')).toBeTruthy();
    expect(testids(host, 'epic-overview-card')).toHaveLength(1);
    (testid(host, 'epic-overview-completed-toggle') as HTMLButtonElement).click();
    const request = http.expectOne((r) => r.url.endsWith('/epics') && r.params.get('status') === 'completed');
    request.flush([rollup({ id: 'done', subTaskTotal: 2, completed: 2 })]);
    fixture.detectChanges();
    expect(fixture.componentInstance.completedEpics().map((e) => e.id)).toEqual(['done']);
    expect(testids(host, 'epic-overview-card')).toHaveLength(2);
    expect(testid(host, 'epic-overview-active-list')?.querySelectorAll('[data-testid="epic-overview-card"]')).toHaveLength(1);
    expect(testid(host, 'epic-overview-completed-list')?.querySelectorAll('[data-testid="epic-overview-card"]')).toHaveLength(1);
    (testid(host, 'epic-overview-completed-toggle') as HTMLButtonElement).click();
    (testid(host, 'epic-overview-completed-toggle') as HTMLButtonElement).click();
    http.verify();
  });

  it('remembers an empty completed result after the first expansion', () => {
    const { fixture, http } = mount(null, [rollup({ id: 'active' })]);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    (testid(host, 'epic-overview-completed-toggle') as HTMLButtonElement).click();
    http.expectOne((r) => r.url.endsWith('/epics') && r.params.get('status') === 'completed').flush([]);
    fixture.detectChanges();
    (testid(host, 'epic-overview-completed-toggle') as HTMLButtonElement).click();
    (testid(host, 'epic-overview-completed-toggle') as HTMLButtonElement).click();
    http.verify();
  });

  it('opens the create dialog from the invite button', () => {
    const { fixture, http } = mount({ name: 'Acme', watchPath: '/repo/acme' }, []);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    (testid(host, 'epic-overview-create') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.showCreate()).toBe(true);
    expect(testid(host, 'epic-create-dialog')).toBeTruthy();
    http.verify();
  });

  it('expands an epic, renders inline sub-task status, and opens the selected sub-task', () => {
    const epics = [
      rollup({
        id: 'E1',
        subTaskTotal: 2,
        open: 1,
        completed: 1,
        subTasks: [
          { id: 'S1', title: 'First sub-task', state: '2-ready', order: 1, orchestratorVerdict: null },
          { id: 'S2', title: 'Reviewed sub-task', state: '5-human-review', order: 2, orchestratorVerdict: 'escalate' },
        ],
      }),
    ];
    const { fixture, http } = mount({ name: 'Acme', watchPath: '/repo/acme' }, epics);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    const opened: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe((event) => opened.push(event));

    expect(testid(host, 'epic-overview-subs')).toBeNull();

    (testid(host, 'epic-overview-expand') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(testid(host, 'epic-overview-subs')).toBeTruthy();
    expect(testids(host, 'epic-overview-open-sub')).toHaveLength(2);
    // AGT-2715: sub-task lane chips render the lane's display name from the
    // presentation catalogue, not the stripped slug ('ready').
    expect(testids(host, 'epic-overview-open-sub')[0].textContent).toContain('Ready');
    expect(testids(host, 'epic-overview-sub-project')).toHaveLength(2);
    expect(testids(host, 'epic-overview-sub-verdict')[0].textContent).toContain('escalate');

    testids(host, 'epic-overview-open-sub')[1].click();
    expect(opened).toEqual([{ jobId: 'S2', watchPath: '/repo/acme' }]);
    http.verify();
  });
});
