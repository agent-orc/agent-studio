import { afterEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiGradingService } from './wiki-grading.service';
import type { WikiGradingRunState, WikiGradingRunStatus } from '../../../../models/project-docs.model';

function status(state: WikiGradingRunState): WikiGradingRunStatus {
  return {
    projectName: 'demo',
    runId: 'run-1',
    state,
    cliType: 'claude',
    model: 'claude-opus-4-8',
    thinkingLevel: null,
    force: false,
    total: 3,
    processed: state === 'running' ? 1 : 3,
    graded: state === 'running' ? 1 : 3,
    skipped: 0,
    failed: 0,
    critical: 0,
    currentRelPath: state === 'running' ? 'docs/model.md' : null,
    startedAtUtc: '2026-09-14T08:00:00Z',
    completedAtUtc: state === 'running' ? null : '2026-09-14T08:05:00Z',
    error: null,
    recent: [],
  };
}

/**
 * AGT-2819 split the Knowledge grading maintenance run out of
 * `project-wiki-section.ts`. What matters here is that a run in flight is
 * followed to its end exactly once, and that a 409 (a run already started
 * elsewhere) is adopted rather than dropped.
 */
function setup() {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      WikiGradingService,
    ],
  });
  return {
    service: TestBed.inject(WikiGradingService),
    http: TestBed.inject(HttpTestingController),
  };
}

const STATUS_URL = '/api/projects/demo/wiki/grading/status';
const RUN_URL = '/api/projects/demo/wiki/grading/run';
const ABORT_URL = '/api/projects/demo/wiki/grading/abort';
const MAINTENANCE_URL = '/api/cli/maintenance-model';

afterEach(() => {
  vi.useRealTimers();
  TestBed.resetTestingModule();
});

describe('WikiGradingService', () => {
  it('seeds the trigger from the workspace maintenance model once per project', () => {
    const { service, http } = setup();

    service.loadContext('demo');

    http.expectOne(MAINTENANCE_URL).flush({
      cliType: 'codex', model: 'gpt-5-codex', thinkingLevel: 'medium',
    });
    http.expectOne(STATUS_URL).flush({ status: null });
    expect(service.cli()).toBe('codex');
    expect(service.model()).toBe('gpt-5-codex');
    expect(service.level()).toBe('medium');

    // A second load for the same project does not re-seed.
    service.loadContext('demo');
    http.expectOne(STATUS_URL).flush({ status: null });
    http.verify();
  });

  it('an unknown CLI in the maintenance model falls back to claude', () => {
    const { service, http } = setup();

    service.loadContext('demo');
    http.expectOne(MAINTENANCE_URL).flush({ cliType: 'nonsense', model: '', thinkingLevel: null });
    http.expectOne(STATUS_URL).flush({ status: null });

    expect(service.cli()).toBe('claude');
    expect(service.model()).toBeNull();
    http.verify();
  });

  it('follows a running run and reports once it finishes', () => {
    vi.useFakeTimers();
    const { service, http } = setup();
    let finished = 0;
    service.onFinished(() => finished++);

    service.start('demo');
    http.expectOne(RUN_URL).flush(status('running'));
    expect(service.status()?.state).toBe('running');

    vi.advanceTimersByTime(1200);
    http.expectOne(STATUS_URL).flush({ status: status('running') });
    expect(finished).toBe(0);

    vi.advanceTimersByTime(1200);
    http.expectOne(STATUS_URL).flush({ status: status('completed') });

    expect(finished).toBe(1);
    expect(service.status()?.state).toBe('completed');
    http.verify();
  });

  it('adopts the live status a 409 returns instead of dropping the run', () => {
    vi.useFakeTimers();
    const { service, http } = setup();

    service.start('demo');
    http.expectOne(RUN_URL).flush(
      { status: status('running') },
      { status: 409, statusText: 'Conflict' },
    );

    expect(service.status()?.state).toBe('running');
    vi.advanceTimersByTime(1200);
    http.expectOne(STATUS_URL).flush({ status: status('completed') });
    http.verify();
  });

  it('only one poll is scheduled at a time', () => {
    vi.useFakeTimers();
    const { service, http } = setup();

    service.start('demo');
    http.expectOne(RUN_URL).flush(status('running'));
    service.start('demo');
    http.expectOne(RUN_URL).flush(status('running'));

    vi.advanceTimersByTime(1200);
    http.expectOne(STATUS_URL).flush({ status: status('completed') });
    http.verify();
  });

  it('an empty project name is a no-op rather than a request', () => {
    const { service, http } = setup();

    service.start('');
    service.abort('');
    service.loadContext('');

    http.verify();
  });

  it('abort records the status the backend reports', () => {
    const { service, http } = setup();

    service.abort('demo');

    http.expectOne(ABORT_URL).flush({ aborted: true, status: status('aborted') });
    expect(service.status()?.state).toBe('aborted');
    http.verify();
  });

  it('setModel treats an empty selection as "inherit"', () => {
    const { service } = setup();

    service.setModel('claude-opus-4-8');
    expect(service.model()).toBe('claude-opus-4-8');
    service.setModel('');
    expect(service.model()).toBeNull();
  });
});
