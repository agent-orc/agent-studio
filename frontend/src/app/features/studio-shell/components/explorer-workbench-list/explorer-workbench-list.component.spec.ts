import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { ExplorerWorkbenchListComponent } from './explorer-workbench-list.component';

function item(id: string, status: WorkbenchListItem['status'], entryPath = `docs/operations/${id}/index.html`): WorkbenchListItem {
  return {
    id, key: `DEM-${id.toUpperCase()}`, title: id, summary: id, status,
    phase: 'testing', updatedAtUtc: '2026-10-01T10:00:00Z', entryPath,
    valid: true, error: null, sourceTaskKeys: [], relatedTaskKeys: [],
    pattern: 'concept', openDecisionCount: status === 'decision-pending' ? 2 : 0,
  };
}

function respond(http: HttpTestingController, items: WorkbenchListItem[]): void {
  http.expectOne(request => request.url === '/api/projects/Demo/workbenches'
    && request.params.get('history') === 'true')
    .flush({ projectName: 'Demo', includesHistory: true, count: items.length, items } satisfies WorkbenchCatalogue);
}

async function mount(activeWorkbenchId: string | null = null, overviewActive = false) {
  await TestBed.configureTestingModule({
    imports: [ExplorerWorkbenchListComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ExplorerWorkbenchListComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.componentRef.setInput('activeWorkbenchId', activeWorkbenchId);
  fixture.componentRef.setInput('overviewActive', overviewActive);
  fixture.detectChanges();
  return { fixture, http: TestBed.inject(HttpTestingController), hub: TestBed.inject(JobsHubClient) };
}

function row(fixture: { nativeElement: HTMLElement }, id: string): HTMLButtonElement {
  return fixture.nativeElement.querySelector(`[data-testid="studio-explorer-project-${id}-Demo"]`) as HTMLButtonElement;
}

describe('ExplorerWorkbenchListComponent', () => {
  it('renders one flat Dossiers row with live decision count and no disclosure', async () => {
    const { fixture, http, hub } = await mount();
    const guide = item('guide', 'active', 'docs/operations/admin-design-guideline/index.html');
    respond(http, [item('pending', 'decision-pending'), guide]);
    fixture.detectChanges();
    expect([...fixture.nativeElement.querySelectorAll('.tree-row__name')].map((label: Element) => label.textContent?.trim()))
      .toEqual(['Dossiers', 'Style Guide']);
    expect(row(fixture, 'workbenches').getAttribute('aria-label')).toBe('Dossiers, 2 decisions waiting');
    expect(row(fixture, 'workbenches').hasAttribute('aria-expanded')).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid^="studio-explorer-workbench-group-"]')).toBeNull();
    hub.workbenchEvent.set({ type: 'decisionRecorded', projectName: 'Demo', workbenchId: 'pending', workbench: null,
      previousStatus: null, occurredAtUtc: new Date().toISOString() });
    fixture.detectChanges();
    respond(http, [item('pending', 'active'), guide]);
    fixture.detectChanges();
    expect(row(fixture, 'workbenches').getAttribute('aria-label')).toBe('Dossiers');
    http.verify();
  });

  it('marks Dossiers current for the overview or an open Dossier, and Style Guide only for its own tab', async () => {
    const { fixture, http } = await mount(null, true);
    const guide = item('guide', 'active', 'docs/operations/admin-design-guideline/index.html');
    respond(http, [guide, item('pending', 'decision-pending')]);
    fixture.detectChanges();
    expect(row(fixture, 'workbenches').getAttribute('aria-current')).toBe('page');
    fixture.componentRef.setInput('overviewActive', false);
    fixture.componentRef.setInput('activeWorkbenchId', 'pending');
    fixture.detectChanges();
    expect(row(fixture, 'workbenches').getAttribute('aria-current')).toBe('page');
    fixture.componentRef.setInput('activeWorkbenchId', 'guide');
    fixture.detectChanges();
    expect(row(fixture, 'workbenches').hasAttribute('aria-current')).toBe(false);
    expect(row(fixture, 'style-guide').getAttribute('aria-current')).toBe('page');
    http.verify();
  });
});
