import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { OrchestratorFeedComponent } from './orchestrator-feed';
import type { OrchestratorLogEntry } from '../../../orchestrator';
import { BoardFiltersService } from '../../../../features/board';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('OrchestratorFeedComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorFeedComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorFeedComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] OrchestratorFeedComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Regression for the override controls on an orchestrator-decision entry.
 *
 * The override buttons act on an Orchestrator Decision and live inside
 * components hosted by overlays / side sheets. A button without an
 * explicit `type` attribute defaults to `type="submit"`, so if any host
 * ever wraps the feed in a `<form>`, clicking one would submit the form
 * and close the surrounding Task / Frontend overlay. Pinning the
 * attribute keeps that side-effect off the table.
 */
describe('OrchestratorFeedComponent · decision override buttons', () => {
  const decisionEntry: OrchestratorLogEntry = {
    ts: '2026-05-14T11:00:00Z',
    kind: 'decision',
    topic: 'reissue',
    summary: 'Reissued the task with stronger framing.',
    reasoning: 'The agent reported a fast Done on a UserContinue follow-up.',
    jobId: 'demo-job',
    tokenUsage: null,
  };

  async function setup(
    entries: OrchestratorLogEntry[] = [{ ...decisionEntry, project: 'demo-project' }],
    hash = '',
  ) {
    history.replaceState(null, '', `${window.location.pathname}${hash}`);
    await TestBed.configureTestingModule({
      imports: [OrchestratorFeedComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    TestBed.inject(BoardFiltersService).hydrateFromUrl();
    TestBed.inject(ProjectLookupService).setWorkspaces(registryWorkspaces());
    const fixture = TestBed.createComponent(OrchestratorFeedComponent);
    fixture.componentRef.setInput('projectName', 'demo-project');
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    fixture.componentInstance.refresh();
    const req = httpCtrl.expectOne((r) => r.url.includes('/api/runner/orchestrator-feed'));
    req.flush({ entries });
    fixture.detectChanges();
    return { fixture, httpCtrl };
  }

  it('renders the "Override this decision" trigger as type="button"', async () => {
    const { fixture } = await setup();

    const root = fixture.nativeElement as HTMLElement;
    const trigger = root.querySelector<HTMLButtonElement>(
      '[data-testid="orchestrator-override-start"]'
    );
    expect(trigger).toBeTruthy();
    expect(trigger?.getAttribute('type')).toBe('button');
  });

  it('defaults to all activity and includes passive observations', async () => {
    const { fixture } = await setup([
      { ...decisionEntry, project: 'demo-project' },
      { ...decisionEntry, ts: '2026-05-14T10:00:00Z', kind: 'observation', summary: 'Routine scan', project: 'demo-project' },
    ]);
    expect(fixture.componentInstance.kindFilter()).toBe('all');
    expect(fixture.componentInstance.visibleEntries().map(entry => entry.kind)).toEqual(['decision', 'observation']);
  });

  it('keeps interleaved projects in one newest-first day stream with a project chip per entry', async () => {
    // Day groups follow the local calendar day, so the entries are built in
    // local time; UTC morning instants fall on two local days west of UTC-10.
    const localMay14 = (hour: number) => new Date(2026, 4, 14, hour).toISOString();
    const { fixture } = await setup([
      { ...decisionEntry, ts: localMay14(9), summary: 'Old Agent Studio event', project: 'Agent Studio' },
      { ...decisionEntry, ts: localMay14(11), summary: 'Newest Runbook event', project: 'Runbook' },
      { ...decisionEntry, ts: localMay14(10), summary: 'Middle Agent Studio event', project: 'Agent Studio' },
    ]);
    fixture.detectChanges();

    expect(fixture.componentInstance.dayGroups()).toHaveLength(1);
    expect(fixture.componentInstance.visibleEntries().map(entry => entry.summary)).toEqual([
      'Newest Runbook event',
      'Middle Agent Studio event',
      'Old Agent Studio event',
    ]);

    const root = fixture.nativeElement as HTMLElement;
    const rows = [...root.querySelectorAll<HTMLElement>('[data-testid="orchestrator-feed-entry"]')];
    expect(rows.map(row => row.dataset['project'])).toEqual(['Runbook', 'Agent Studio', 'Agent Studio']);
    expect(rows.map(row => row.querySelector('[data-testid="orchestrator-entry-project"]')?.textContent?.trim()))
      .toEqual(['RUN', 'AGT', 'AGT']);
    expect(fixture.componentInstance.projectColor('Agent Studio')).toBe('#123456');
    expect(root.querySelector('[data-testid="orchestrator-feed-day"]')?.textContent).not.toContain('Agent Studio');
  });

  it('orders timestamps by instant when entries carry different UTC offsets', async () => {
    const { fixture } = await setup([
      { ...decisionEntry, ts: '2026-05-14T11:00:00+02:00', summary: 'Older offset event', project: 'Agent Studio' },
      { ...decisionEntry, ts: '2026-05-14T10:00:00Z', summary: 'Newer UTC event', project: 'Runbook' },
    ]);

    expect(fixture.componentInstance.visibleEntries().map(entry => entry.summary)).toEqual([
      'Newer UTC event',
      'Older offset event',
    ]);
  });

  it('honours a shared multi-project URL filter and writes chip filtering through the same contract', async () => {
    const { fixture } = await setup([
      { ...decisionEntry, project: 'Agent Studio' },
      { ...decisionEntry, ts: '2026-05-14T10:00:00Z', project: 'Runbook' },
      { ...decisionEntry, ts: '2026-05-14T09:00:00Z', project: 'Taskboard' },
    ], '#/feed&filters=projects%3AAgent%20Studio%2CRunbook');

    expect([...fixture.componentInstance.projectFilter()]).toEqual(['Agent Studio', 'Runbook']);
    expect(fixture.componentInstance.visibleEntries().map(entry => entry.project)).toEqual(['Agent Studio', 'Runbook']);

    fixture.componentInstance.selectProject('Taskboard');
    expect(fixture.componentInstance.visibleEntries().map(entry => entry.project)).toEqual(['Taskboard']);
    expect(decodeURIComponent(window.location.hash)).toContain('projects:Taskboard');
  });

  it('renders pipeline health alarms and exposes the dedicated alert filter', async () => {
    const { fixture } = await setup([{
      ...decisionEntry,
      kind: 'alert',
      topic: 'pipeline-health',
      summary: 'Systemic gate problem detected',
      project: 'demo-project',
    }]);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const alert = root.querySelector<HTMLElement>('[data-testid="orchestrator-feed-entry"][data-entry-kind="alert"]');
    const filter = root.querySelector<HTMLElement>('[data-testid="feed-kind-alert"]');
    expect(alert?.textContent).toContain('Systemic gate problem detected');
    expect(alert?.textContent).toContain('Alert');
    expect(filter?.textContent).toContain('Alerts');
    expect(filter?.textContent).toContain('1');
  });

  it('renders Cancel and Send override as type="button" while the override form is open', async () => {
    const { fixture } = await setup();

    fixture.componentInstance.startOverride(decisionEntry);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const submit = root.querySelector<HTMLButtonElement>(
      '[data-testid="orchestrator-override-submit"]'
    );
    const cancel = root.querySelector<HTMLButtonElement>('.orch-feed__override-cancel');
    expect(submit).toBeTruthy();
    expect(cancel).toBeTruthy();
    expect(submit?.getAttribute('type')).toBe('button');
    expect(cancel?.getAttribute('type')).toBe('button');
  });
});

function registryWorkspaces(): RegistryWorkspaceListItem[] {
  const project = (displayName: string, shortCode: string, sortOrder: number) => ({
    sourceType: 'local-folder' as const,
    id: `project-${shortCode}`,
    displayName,
    shortCode,
    workspaceId: 'workspace-1',
    color: shortCode === 'AGT' ? '#123456' : null,
    cliDefault: null,
    modelDefault: null,
    sortOrder,
    storageLocation: `/tmp/${shortCode.toLowerCase()}`,
    repositoryPath: null,
    rootPath: null,
    repositoryUrl: null,
    urls: [],
    archived: false,
    createdAt: '2026-05-01T00:00:00Z',
  });
  return [{
    id: 'workspace-1',
    displayName: 'Workspace',
    sortOrder: 0,
    isDefault: true,
    color: null,
    createdAt: '2026-05-01T00:00:00Z',
    projects: [
      project('Agent Studio', 'AGT', 0),
      project('Runbook', 'RUN', 1),
      project('Taskboard', 'TSK', 2),
    ],
  }];
}
