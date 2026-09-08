import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TaskService, orchestratorContextChatSegment } from './task.service';
import { ErrorDialogService } from './error-dialog.service';
import { JobsHubClient, type JobsHubHandlers } from './jobs-hub-client.service';
import type { TaskInfo } from '../models/task.model';

class ErrorDialogServiceStub {
  show(): void { return undefined; }
}

class JobsHubClientStub {
  readonly connected = signal(false);
  handlers: JobsHubHandlers | null = null;
  start(handlers: JobsHubHandlers): void {
    this.handlers = handlers;
  }
  stop(): void { return undefined; }
}

const emptyGrouped = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  autoReview: [],
  humanReview: [],
  escalated: [],
  review: [],
  completed: [],
  archive: [],
};

describe('TaskService', () => {
  let service: TaskService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ErrorDialogService, useClass: ErrorDialogServiceStub },
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    });

    service = TestBed.inject(TaskService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('decodes job file content from UTF-8 bytes', () => {
    const expected = 'Lücken / gehört / für / „Anführung"';
    let actual = '';

    service.readJobFile('demo-job', 'prompt.md', 'C:/projects/demo').subscribe((text) => {
      actual = text;
    });

    const req = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/files/prompt.md' &&
      request.params.get('watchPath') === 'C:/projects/demo');
    expect(req.request.responseType).toBe('arraybuffer');

    req.flush(utf8Buffer(expected), {
      headers: { 'Content-Type': 'text/plain' },
    });

    expect(actual).toBe(expected);
  });

  it('loads task detail by project handle and falls back to watchPath', () => {
    let detailId = '';
    service.getDetailByProject('epic-a', 'PROJ-002', 'C:/projects/demo').subscribe((detail) => {
      detailId = detail.info.id;
    });

    const projectRequest = http.expectOne((request) =>
      request.url === '/api/tasks/epic-a' &&
      request.params.get('project') === 'PROJ-002' &&
      !request.params.has('watchPath'));
    projectRequest.flush(null, { status: 404, statusText: 'Not Found' });

    const fallbackRequest = http.expectOne((request) =>
      request.url === '/api/tasks/epic-a' &&
      request.params.get('watchPath') === 'C:/projects/demo' &&
      !request.params.has('project'));
    fallbackRequest.flush({ info: { id: 'epic-a' } });

    expect(detailId).toBe('epic-a');
  });

  it('decodes raw diff endpoints from UTF-8 bytes', () => {
    const expected = '+überarbeitete Zeile';
    let actual = '';

    service.getGitDiff('demo-job', 'src/file.ts', 'C:/projects/demo').subscribe((text) => {
      actual = text;
    });

    const req = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/git/diff' &&
      request.params.get('watchPath') === 'C:/projects/demo' &&
      request.params.get('path') === 'src/file.ts');
    expect(req.request.responseType).toBe('arraybuffer');

    req.flush(utf8Buffer(expected), {
      headers: { 'Content-Type': 'text/plain' },
    });

    expect(actual).toBe(expected);
  });

  it('rehydrates runner status through the SignalR reconnect hook', () => {
    const hub = TestBed.inject(JobsHubClient) as unknown as JobsHubClientStub;

    service.startLiveUpdates();
    http.expectOne('/api/projects/settings').flush({});

    expect(hub.handlers?.reconnected).toBeTruthy();
    hub.handlers?.reconnected?.();

    http.expectOne('/api/tasks/grouped').flush(emptyGrouped);
    http.expectNone('/api/tasks');
    http.expectOne('/api/runner/status').flush({
      projects: {
        demo: {
          projectName: 'demo',
          mode: 'auto-continuous',
          activeJobId: 'job-1',
          activeExecution: null,
          queuedJobIds: ['job-2'],
        },
      },
    });

    expect(service.runnerStatus().projects['demo'].activeJobId).toBe('job-1');
    expect(service.runnerStatus().projects['demo'].mode).toBe('auto-continuous');
    service.stopLiveUpdates();
  });

  it('builds flat and grouped board state from one grouped request', () => {
    const task = {
      id: 'job-1',
      taskKey: 'DEMO-1',
      watchPath: 'C:/projects/demo/.orchestrator/jobs',
      state: '4-auto-review',
      title: 'Single snapshot',
    } as TaskInfo;

    service.refresh();

    const grouped = {
      ...emptyGrouped,
      autoReview: [task],
      review: [task],
    };
    http.expectOne('/api/tasks/grouped').flush(grouped);
    http.expectNone('/api/tasks');
    http.expectOne('/api/runner/status').flush({ projects: {} });

    expect(service.grouped().autoReview).toEqual([task]);
    expect(service.jobs()).toEqual([task]);
    expect(service.loading()).toBe(false);
  });

  it('coalesces overlapping board and runner refreshes into one trailing request', () => {
    service.refresh(true);
    service.refresh(true);
    service.refresh(true);

    const firstGrouped = http.expectOne('/api/tasks/grouped');
    const firstRunner = http.expectOne('/api/runner/status');

    firstGrouped.flush(emptyGrouped);
    firstRunner.flush({ projects: {} });

    http.expectOne('/api/tasks/grouped').flush(emptyGrouped);
    http.expectOne('/api/runner/status').flush({ projects: {} });
    http.expectNone('/api/tasks/grouped');
    http.expectNone('/api/runner/status');
  });

  it('calls the file-source history endpoints with encoded paths and source params', () => {
    let historyCount = 0;
    service
      .getTaskFileHistory('demo-job', 'results/review note.md', 'C:/projects/demo', 'workspace')
      .subscribe((entries) => {
        historyCount = entries.length;
      });

    const historyReq = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/files/results/review%20note.md/history' &&
      request.params.get('watchPath') === 'C:/projects/demo' &&
      request.params.get('scope') === 'workspace');
    historyReq.flush([{ sha: 'abcdef1', at: '2026-06-09T12:00:00Z', message: 'capture', author: 'A', provenance: { source: 'workspace', path: 'results/review note.md' } }]);
    expect(historyCount).toBe(1);

    let version = '';
    service
      .readTaskFileAt('demo-job', 'results/review note.md', 'abcdef1', 'C:/projects/demo', 'workspace')
      .subscribe((text) => {
        version = text;
      });

    const versionReq = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/files/results/review%20note.md' &&
      request.params.get('watchPath') === 'C:/projects/demo' &&
      request.params.get('scope') === 'workspace' &&
      request.params.get('at') === 'abcdef1');
    expect(versionReq.request.responseType).toBe('arraybuffer');
    versionReq.flush(utf8Buffer('# Old review\n'));
    expect(version).toBe('# Old review\n');
  });

  // ASS-1727: the paged Archive read endpoint. The board's grouped.archive is
  // intentionally empty, so the Archive view reads here. Verify the query is
  // built correctly (offset/limit/trimmed search) and the envelope passes
  // through untouched.
  it('pages the archive endpoint with offset/limit and a trimmed search term', () => {
    let total = -1;
    let ids: string[] = [];
    service.getArchivedTasks({ offset: 50, limit: 25, search: '  migration  ' }).subscribe((res) => {
      total = res.total;
      ids = res.items.map((i) => i.id);
    });

    const req = http.expectOne((request) =>
      request.url === '/api/tasks/archive' &&
      request.params.get('offset') === '50' &&
      request.params.get('limit') === '25' &&
      request.params.get('search') === 'migration');
    req.flush({
      items: [{ id: 'arch-1', taskKey: 't::arch-1', title: 'Archived', state: '7-archive' }],
      total: 873,
      offset: 50,
      limit: 25,
    });

    expect(total).toBe(873);
    expect(ids).toEqual(['arch-1']);
  });

  it('omits empty optional params from the archive query', () => {
    service.getArchivedTasks().subscribe();

    const req = http.expectOne((request) => request.url === '/api/tasks/archive');
    expect(req.request.params.has('offset')).toBe(false);
    expect(req.request.params.has('limit')).toBe(false);
    expect(req.request.params.has('search')).toBe(false);
    expect(req.request.params.has('watchPath')).toBe(false);
    req.flush({ items: [], total: 0, offset: 0, limit: 50 });
  });

  // MC-2 (Concept §4): per-context transcript history. The side sheet derives
  // a `project:<PROJ>` / `task:<PROJ>/<KEY>` context key from navigation and
  // reads it through GET /api/runner/{contextKey}/orchestrator-chat.
  it('reads a project-context transcript by context key', () => {
    let received: string[] = [];
    service.getOrchestratorChatByContext('project:Agent Studio').subscribe((r) => {
      received = r.turns.map((t) => t.text);
    });

    const req = http.expectOne('/api/runner/project:Agent%20Studio/orchestrator-chat');
    expect(req.request.method).toBe('GET');
    req.flush({
      project: 'Agent Studio',
      turns: [{ id: '1', ts: '2026-07-09T00:00:00Z', role: 'user', text: 'board' }],
    });

    expect(received).toEqual(['board']);
  });

  it('reads a task-context transcript with proj + key as separate segments', () => {
    service.getOrchestratorChatByContext('task:Agent Studio/AGT-1916').subscribe();

    const req = http.expectOne('/api/runner/task:Agent%20Studio/AGT-1916/orchestrator-chat');
    expect(req.request.method).toBe('GET');
    req.flush({ project: 'Agent Studio', turns: [] });
  });

  it('sends a task-context message to the context route so it lands in its own thread', () => {
    let reply = '';
    service
      .sendOrchestratorChatByContext('task:Agent Studio/AGT-1916', { text: 'where do you stand?' })
      .subscribe((r) => {
        reply = r.reply.text ?? '';
      });

    const req = http.expectOne('/api/runner/task:Agent%20Studio/AGT-1916/orchestrator-chat');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ text: 'where do you stand?' });
    req.flush({
      project: 'Agent Studio',
      reply: { id: '2', ts: '2026-07-09T00:00:01Z', role: 'orchestrator', text: 'on the header' },
    });

    expect(reply).toBe('on the header');
  });

  it('reads a project-scoped orchestrator context digest with safe path encoding', () => {
    let digest = '';
    service.getOrchestratorContextDigest('project:Agent Studio').subscribe(value => {
      digest = value.digest;
    });

    const req = http.expectOne('/api/orchestrator/context/project:Agent%20Studio');
    expect(req.request.method).toBe('GET');
    req.flush({
      contextKey: 'project:Agent Studio',
      capturedAt: '2026-07-11T10:00:00Z',
      digest: 'lanes: ready=2',
      sources: [],
    });

    expect(digest).toBe('lanes: ready=2');
  });

  it('force-refreshes global and task orchestrator context digests explicitly', () => {
    service.refreshOrchestratorContextDigest('global').subscribe();
    const global = http.expectOne('/api/orchestrator/context/global/refresh');
    expect(global.request.method).toBe('POST');
    expect(global.request.body).toBeNull();
    global.flush({
      contextKey: 'global',
      capturedAt: '2026-07-11T10:00:00Z',
      digest: 'workspace',
      sources: [],
    });

    service.refreshOrchestratorContextDigest('task:Agent Studio/AGT-2047').subscribe();
    const task = http.expectOne('/api/orchestrator/context/task:Agent%20Studio/AGT-2047/refresh');
    expect(task.request.method).toBe('POST');
    task.flush({
      contextKey: 'task:Agent Studio/AGT-2047',
      capturedAt: '2026-07-11T10:01:00Z',
      digest: 'task focus',
      sources: [],
    });
  });

  it('sends an operator decision reason through the existing move request', () => {
    service
      .moveJob(
        'AGT-2355',
        '6-completed',
        'C:/projects/demo',
        undefined,
        'Decision icon-source: selected "Keep current glyphs".',
      )
      .subscribe();

    const req = http.expectOne((request) =>
      request.url === '/api/tasks/AGT-2355/move'
      && request.params.get('watchPath') === 'C:/projects/demo');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      targetState: '6-completed',
      reason: 'Decision icon-source: selected "Keep current glyphs".',
    });
    req.flush({});
  });

  it('sends the explicit integration override only when requested', () => {
    service
      .moveJob(
        'AGT-2543',
        '6-completed',
        'C:/projects/demo',
        undefined,
        'No delivery branch is expected.',
        true,
      )
      .subscribe();

    const req = http.expectOne((request) =>
      request.url === '/api/tasks/AGT-2543/move'
      && request.params.get('watchPath') === 'C:/projects/demo');
    expect(req.request.body).toEqual({
      targetState: '6-completed',
      reason: 'No delivery branch is expected.',
      operatorOverride: true,
    });
    req.flush({});
  });

  it('queues a batch move and reads its progress by handle', () => {
    const items = [
      { jobId: 'alpha', watchPath: 'C:/projects/demo', targetState: '7-archive' },
      { jobId: 'beta', watchPath: 'C:/projects/demo', targetState: '7-archive' },
    ];
    let batchId = '';

    service.startBatchMove(items).subscribe((job) => { batchId = job.id; });
    const start = http.expectOne('/api/tasks/batch-move');
    expect(start.request.method).toBe('POST');
    expect(start.request.body).toEqual({ items });
    start.flush({ id: 'batch-123', status: 'queued', total: 2, completed: 0, results: [] });
    expect(batchId).toBe('batch-123');

    service.getBatchMove(batchId).subscribe();
    const progress = http.expectOne('/api/tasks/batch-move/batch-123');
    expect(progress.request.method).toBe('GET');
    progress.flush({ id: batchId, status: 'running', total: 2, completed: 1, results: [] });
  });
});

describe('orchestratorContextChatSegment', () => {
  it('encodes each id part while preserving the prefix and structural slash', () => {
    expect(orchestratorContextChatSegment('project:Agent Studio')).toBe('project:Agent%20Studio');
    expect(orchestratorContextChatSegment('task:Agent Studio/AGT-1916')).toBe(
      'task:Agent%20Studio/AGT-1916',
    );
  });

  it('encodes a workbench (Dossier) key the same way as a task key, as two route segments', () => {
    expect(orchestratorContextChatSegment('workbench:Agent Studio/AGT-W43')).toBe(
      'workbench:Agent%20Studio/AGT-W43',
    );
  });

  it('falls back to a single encoded segment for unrecognized shapes', () => {
    expect(orchestratorContextChatSegment('global')).toBe('global');
    expect(orchestratorContextChatSegment('task:no-slash')).toBe('task%3Ano-slash');
    expect(orchestratorContextChatSegment('workbench:no-slash')).toBe('workbench%3Ano-slash');
  });
});

function utf8Buffer(value: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(value);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}
