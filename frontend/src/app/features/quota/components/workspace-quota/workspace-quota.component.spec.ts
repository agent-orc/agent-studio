import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { WorkspaceQuotaComponent } from './workspace-quota.component';

const usage = (modelTokens: number) => ({ modelTokens, modelCostUsd: 0, computeCoreHours: 0, hostingSlots: 0 });
// fixed-date: quota week fixture (Monday 12 Oct 2026 UTC), not a clock read
const WEEK_START = '2026-10-12T00:00:00Z';

function snapshot(overrides: Record<string, unknown> = {}) {
  return {
    workspaceId: 'ws-a', projectId: null, weekStartUtc: WEEK_START, weekEndUtc: '2026-10-19T00:00:00Z',
    limits: { modelTokensPerWeek: 100, computeCoreHoursPerWeek: null, hostingSlots: null },
    usage: usage(40), remaining: usage(60), status: 'ok', biggestConsumers: [], biggestProjects: [],
    dailyTrend: Array.from({ length: 7 }, (_, index) => usage(index * 10)),
    meterComplete: true, overrideActive: false, codingAllowed: true, hostingAllowed: true, message: null,
    ...overrides,
  };
}

describe('WorkspaceQuotaComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [WorkspaceQuotaComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  async function render(workspaceId = 'ws-a', projectId: string | null = null) {
    const fixture = TestBed.createComponent(WorkspaceQuotaComponent);
    fixture.componentRef.setInput('workspaceId', workspaceId);
    fixture.componentRef.setInput('projectId', projectId);
    await fixture.whenStable();
    return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
  }
  const text = (root: HTMLElement, testId: string) => root.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim() ?? null;

  it('dates every trend row from the UTC week start', async () => {
    const { fixture, root } = await render();
    http.expectOne('/api/workspaces/ws-a/quota').flush(snapshot());
    await fixture.whenStable();

    const rows = Array.from(root.querySelectorAll('.workspace-quota__trend > span'));
    expect(rows).toHaveLength(7);
    expect(rows[0].textContent).toContain('Mon 12 Oct');
    expect(rows[0].querySelector('time')?.getAttribute('datetime')).toBe('2026-10-12');
    expect(rows[6].textContent).toContain('Sun 18 Oct');
    expect(rows[6].textContent).toContain('60 tokens');
    expect(root.textContent).not.toContain('Day 1');
  });

  it('ignores an answer superseded by a later refresh', async () => {
    const { fixture, component, root } = await render();
    const first = http.expectOne('/api/workspaces/ws-a/quota');
    component.refresh();
    const second = http.expectOne('/api/workspaces/ws-a/quota');

    second.flush(snapshot({ status: 'warning', message: 'Workspace A used 85 % of its weekly model tokens.' }));
    first.flush(snapshot({ status: 'blocked', message: 'Weekly quota reached.' }));
    await fixture.whenStable();

    expect(component.snapshot()?.status).toBe('warning');
    expect(text(root, 'workspace-quota-state')).toBe('Warning');
  });

  it('ignores the previous scope once the project input changes', async () => {
    const { fixture, component } = await render('ws-a', 'p-alpha');
    const alpha = http.expectOne('/api/workspaces/ws-a/projects/p-alpha/quota');

    fixture.componentRef.setInput('projectId', 'p-beta');
    alpha.flush(snapshot({ projectId: 'p-alpha', status: 'blocked' }));
    expect(component.snapshot()).toBeNull();

    await fixture.whenStable();
    http.expectOne('/api/workspaces/ws-a/projects/p-beta/quota').flush(snapshot({ projectId: 'p-beta' }));
    expect(component.snapshot()?.projectId).toBe('p-beta');
  });

  it('drops the previous workspace snapshot at once when the workspace input changes', async () => {
    const { fixture, component } = await render('ws-a');
    http.expectOne('/api/workspaces/ws-a/quota').flush(snapshot({ status: 'blocked' }));
    await fixture.whenStable();
    expect(component.snapshot()?.workspaceId).toBe('ws-a');

    fixture.componentRef.setInput('workspaceId', 'ws-b');
    await fixture.whenStable();
    expect(component.snapshot()).toBeNull();
    http.expectOne('/api/workspaces/ws-b/quota').flush(snapshot({ workspaceId: 'ws-b' }));
    expect(component.snapshot()?.workspaceId).toBe('ws-b');
  });

  it('hides the meter line without a limit and shows it when a limit waits for the meter', async () => {
    const { fixture, component, root } = await render();
    http.expectOne('/api/workspaces/ws-a/quota').flush(snapshot({ limits: null, meterComplete: false }));
    await fixture.whenStable();
    expect(text(root, 'workspace-quota-meter')).toBeNull();
    expect(text(root, 'workspace-quota-state')).toBe('No limit');

    component.refresh();
    http.expectOne('/api/workspaces/ws-a/quota').flush(snapshot({ meterComplete: false }));
    await fixture.whenStable();
    expect(text(root, 'workspace-quota-meter')).toContain('Admission waits for the meter');
  });

  it('labels full hosting slots and shows their message', async () => {
    const message = 'All 2 hosting slots are in use; new deploys wait until an environment stops.';
    const { fixture, root } = await render();
    http.expectOne('/api/workspaces/ws-a/quota').flush(snapshot({
      status: 'hosting-full', message, hostingAllowed: false,
      limits: { modelTokensPerWeek: null, computeCoreHoursPerWeek: null, hostingSlots: 2 },
    }));
    await fixture.whenStable();

    expect(root.querySelector('[data-testid="workspace-quota"]')?.getAttribute('data-status')).toBe('hosting-full');
    expect(text(root, 'workspace-quota-state')).toBe('Hosting slots full');
    expect(root.textContent).toContain(message);
  });
});
