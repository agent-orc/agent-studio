import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, type HttpRequest } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import type { TaskInfo } from '../../../models/task.model';
import type { ResourceName } from '../../../models/task-core.model';
import { AuthSessionState } from '../../../services/auth.service';
import { TaskSelectionService } from './task-selection.service';

/**
 * AGT-3669: a refused detail section answers for that section only. Only a
 * refused core read clears the selection, and its message names access, not
 * the connection. A Product session does not request the sections it may not
 * read.
 */
describe('TaskSelectionService · section access', () => {
  let selection: TaskSelectionService;
  let http: HttpTestingController;

  const info = {
    id: 'product-card', key: 'DEMO-5', taskKey: 'C:\\demo::product-card', title: 'Add CSV export',
    state: '5-human-review', order: 1, watchPath: 'C:\\demo', projectName: 'Demo App',
  } as unknown as TaskInfo;
  const core = {
    state: 'ready', projectId: 'Demo App', projectName: info.projectName, id: info.id,
    taskKey: info.taskKey, key: info.key, title: info.title, watchPath: info.watchPath,
    folderPath: `${info.watchPath}\\${info.id}`, kind: 'task', taskType: 'feature', lane: info.state,
    archiveState: null, enteredLaneAt: '2026-11-09T00:00:00Z', order: 1, mode: 'coding',
    released: false, pendingIntent: false, coreVersion: '1',
    pins: { model: null, modelExplicit: false, thinkingLevel: null, thinkingLevelExplicit: false,
      cliType: null, contextMode: null, useOwnSession: null, allowWebAccess: false, noBranchExpected: false },
    blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null,
      executionStatus: null, location: 'none', heartbeatAt: null, leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'ready', text: 'Delivered the CSV export.', originalBytes: 25, hash: null, cursor: null },
    prompt: { state: 'ready', text: 'Add a CSV export to reports.', originalBytes: 28, hash: null, cursor: null,
      continuationUrl: null },
    timeline: { state: 'missing', events: [], cursor: null, continuationUrl: null },
  };
  const reply = (resource: string, data: unknown) => ({
    id: info.id, taskKey: info.taskKey, projectId: 'Demo App', attemptId: null, coreVersion: '1',
    resource, version: 'v1', computedAt: null, state: 'ready', reason: null, data,
  });
  const forbidden = { status: 403, statusText: 'Forbidden' };
  const coreRead = (req: HttpRequest<unknown>) => req.url.endsWith(`/${info.id}/core`);
  const section = (name: string) => (req: HttpRequest<unknown>) => req.url.endsWith(`/${info.id}/details/${name}`);
  const loading = (name: ResourceName) => vi.waitFor(
    () => expect(selection.resourceStates()[name].phase).toBe('loading'), { timeout: 5000 });

  const openCard = () => {
    selection.openDetail(info);
    http.expectOne(coreRead).flush(core);
  };
  const flushDocuments = async () => {
    await loading('documents');
    for (const request of http.match(section('documents'))) {
      const name = request.request.params.get('name')!;
      request.flush(reply('documents', { name, markdown: `${name} markdown`, summaryState: null }));
    }
  };
  const flushOverviewAndReview = async () => {
    await loading('overview');
    http.expectOne(section('overview')).flush(reply('overview', {}));
    http.expectOne(section('review')).flush(reply('review', { reviewProjection: null, evidence: null }));
  };

  beforeEach(async () => {
    sessionStorage.clear();
    history.replaceState(null, '', '/');
    await TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    selection = TestBed.inject(TaskSelectionService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    try {
      http.verify();
    } finally {
      history.replaceState(null, '', '/');
      TestBed.resetTestingModule();
    }
  });

  it('keeps the card open and hides only an optional section answered 403', async () => {
    openCard();
    await flushDocuments();
    await loading('usage');
    http.expectOne(section('usage')).flush({ error: 'forbidden' }, forbidden);
    await flushOverviewAndReview();

    expect(selection.resourceStates().usage.phase).toBe('restricted');
    expect(selection.selected()?.promptMarkdown).toBe('prompt markdown');
    expect(selection.detailLoadError()).toBeNull();
    // One core revalidation decides whether the card itself is still readable.
    http.expectOne(coreRead).flush(core);
    expect(selection.selectedCore()?.id).toBe(info.id);
    expect(selection.detailLoadError()).toBeNull();
    // A restricted section is not requested again for the same generation.
    selection.loadResource('usage');
    http.expectNone(section('usage'));
  });

  it('marks refused documents and paints the card from its core', async () => {
    openCard();
    await loading('documents');
    for (const request of http.match(section('documents'))) request.flush({ error: 'forbidden' }, forbidden);
    http.expectOne(coreRead).flush(core);
    await loading('usage');
    http.expectOne(section('usage')).flush(reply('usage', { contextUsage: null }));
    await flushOverviewAndReview();

    expect(selection.resourceStates().documents).toEqual({ phase: 'unavailable', reason: 'document-access-denied' });
    expect(selection.selected()?.promptMarkdown).toBe('Add a CSV export to reports.');
    expect(selection.selected()?.statusMarkdown).toBe('Delivered the CSV export.');
    expect(selection.detailLoadError()).toBeNull();
  });

  it('clears the selection only when the core read is refused, and names access', async () => {
    openCard();
    await flushDocuments();
    await loading('usage');
    http.expectOne(section('usage')).flush({ error: 'forbidden' }, forbidden);
    await flushOverviewAndReview();
    expect(selection.selected()).not.toBeNull();

    http.expectOne(coreRead).flush({ error: 'forbidden' }, forbidden);

    expect(selection.selected()).toBeNull();
    expect(selection.selectedCore()).toBeNull();
    expect(selection.detailLoadError()?.message).toBe('This card is not available to this account.');
  });

  it.each([403, 404])('names access, not the connection, when the core read answers %i', status => {
    selection.openDetail(info);
    http.expectOne(coreRead).flush({ state: status === 404 ? 'missing' : 'denied' },
      { status, statusText: status === 404 ? 'Not Found' : 'Forbidden' });

    expect(selection.selected()).toBeNull();
    expect(selection.detailLoadError()?.message).toBe('This card is not available to this account.');
  });

  it('does not request usage for a Product session', async () => {
    TestBed.inject(AuthSessionState).status.set({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_product', username: 'carla', displayName: 'Carla', role: 'contributor',
        projects: ['demo-app'], disabled: false, mustChangePassword: false },
    });
    openCard();
    await flushDocuments();
    await flushOverviewAndReview();

    http.expectNone(section('usage'));
    expect(selection.resourceStates().usage).toEqual({ phase: 'restricted', reason: 'section-access-denied' });
    expect(selection.selected()?.promptMarkdown).toBe('prompt markdown');
    expect(selection.detailLoadError()).toBeNull();
  });
});
