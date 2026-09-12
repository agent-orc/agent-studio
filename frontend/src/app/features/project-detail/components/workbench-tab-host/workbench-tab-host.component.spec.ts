import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { WorkbenchDocument } from '../../../../models/project-docs.model';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { WorkbenchTabHostComponent } from './workbench-tab-host.component';

const DOCUMENT: WorkbenchDocument = {
  workbench: {
    id: 'runner-link',
    key: 'AGT-W43',
    title: 'Runner Link Health',
    summary: 'Dossier chat isolation proof.',
    status: 'decided',
    phase: 'testing',
    updatedAtUtc: '2026-09-06T10:00:00Z',
    entryPath: 'docs/workbenches/runner-link/index.html',
    valid: true,
    error: null,
    sourceTaskKeys: [],
    relatedTaskKeys: [],
  },
  html: '<h1>Runner Link Health</h1>',
  branch: 'main',
  revision: null,
  workingTreeModified: false,
  fingerprint: null,
};

/**
 * AGT-2725: a Dossier tab opened by route (deep link or reload) starts
 * without the catalogue `key` a click-opened tab already carries (see
 * `app.ts` `onWorkbenchOverviewOpen` vs the `workbench` route-open branch).
 * The orchestrator side sheet derives its Dossier scope from that key via
 * `composerContext().referenceKey`, so a route-opened tab must have it
 * backfilled once the viewer resolves the document.
 */
describe('WorkbenchTabHostComponent', () => {
  beforeEach(() => localStorage.removeItem('atp.studio.tabs.v1'));

  it('backfills the active tab with the resolved catalogue key and title', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchTabHostComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const tabState = TestBed.inject(StudioTabStateService);
    tabState.open({ kind: 'workbench', projectName: 'Demo', workbenchId: 'runner-link' });

    const fixture = TestBed.createComponent(WorkbenchTabHostComponent);
    fixture.componentRef.setInput('mode', 'viewer');
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('workbenchId', 'runner-link');
    fixture.detectChanges();

    fixture.componentInstance.onDocumentResolved(DOCUMENT);

    const tab = tabState.tabs().find(t => t.kind === 'workbench');
    expect(tab).toMatchObject({
      kind: 'workbench',
      projectName: 'Demo',
      workbenchId: 'runner-link',
      key: 'AGT-W43',
      title: 'Runner Link Health',
    });
    // The tab identity (project + workbenchId) is unchanged, so this stays
    // the same single tab rather than opening a duplicate.
    expect(tabState.tabs().filter(t => t.kind === 'workbench')).toHaveLength(1);
  });

  it('does nothing when the project or workbench input is missing', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchTabHostComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const tabState = TestBed.inject(StudioTabStateService);
    const before = tabState.tabs();

    const fixture = TestBed.createComponent(WorkbenchTabHostComponent);
    fixture.componentRef.setInput('mode', 'viewer');
    fixture.detectChanges();
    fixture.componentInstance.onDocumentResolved(DOCUMENT);

    expect(tabState.tabs()).toEqual(before);
  });
});
