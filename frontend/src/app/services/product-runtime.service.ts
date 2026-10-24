import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';
import {
  ProductRuntimeEvent,
  RuntimeEventListResponse,
  RuntimeEventParseWarning,
} from '../models/product-runtime.model';

/**
 * Read-only client for the project-screen Product Runtime Observability
 * panel. Wraps {@code GET /api/runtime/{project}/events} from
 * {@code RuntimeEventEndpoints.cs}. Every request is best-effort and falls
 * back to an empty payload on transport error so the UI stays calm in
 * offline / dev scenarios, mirroring {@link AgentBusService}.
 */
@Injectable({ providedIn: 'root' })
export class ProductRuntimeService {
  private readonly http = inject(HttpClient);

  getTaskDetailLatency(): Observable<TaskDetailLatency> {
    return this.http.get<TaskDetailLatency>('/api/runtime/task-detail-latency');
  }

  getEvents(project: string, refresh = false): Observable<RuntimeEventListResponse> {
    const url = `/api/runtime/${encodeURIComponent(project)}/events`;
    let params = new HttpParams();
    if (refresh) params = params.set('refresh', 'true');
    return this.http.get<RuntimeEventListResponse>(url, { params }).pipe(
      map(r => r ?? this.emptyResponse(project)),
      catchError(() => of(this.emptyResponse(project))),
    );
  }

  private emptyResponse(project: string): RuntimeEventListResponse {
    return { project, events: [], warnings: [] };
  }
}

export interface TaskDetailLatency {
  count: number;
  p50Ms: number | null;
  p95Ms: number | null;
  maxMs: number | null;
}

/**
 * Synthetic dataset used when the live runtime stream is empty. Sized to
 * exercise the surfaces (counters, timeline, error groups, latency
 * summary, malformed warnings) without waiting for a real producer to
 * emit. Toggled per-component; this fixture is never injected on top of a
 * live, non-empty stream.
 */
export const ProductRuntimeFixture = {
  sample(project: string): RuntimeEventListResponse {
    const now = Date.now();
    const at = (offsetMs: number) => new Date(now - offsetMs).toISOString();

    const events: ProductRuntimeEvent[] = [
      {
        schemaVersion: 1,
        timestamp: at(60_000),
        level: 'Info',
        event: 'http.request.completed',
        subsystem: 'backend',
        operation: 'GET /api/tasks',
        correlationId: 'req-001',
        project,
        jobId: 'sample-runtime-job',
        runId: 'run-001',
        duration: { ms: 42 },
        status: 'Ok',
        tags: ['route:jobs'],
        payload: { route: '/api/tasks', statusCode: 200 },
      },
      {
        schemaVersion: 1,
        timestamp: at(55_000),
        level: 'Info',
        event: 'render.first-paint',
        subsystem: 'frontend',
        operation: 'kanban-board',
        correlationId: 'req-001',
        project,
        duration: { ms: 318 },
        status: 'Ok',
        tags: ['view:kanban'],
      },
      {
        schemaVersion: 1,
        timestamp: at(50_000),
        level: 'Warn',
        event: 'job.created',
        subsystem: 'backend',
        operation: 'JobsEndpoint.Create',
        correlationId: 'req-002',
        project,
        jobId: 'sample-runtime-job',
        duration: { ms: 91 },
        status: 'Ok',
        tags: ['lane:1-preparation'],
      },
      {
        schemaVersion: 1,
        timestamp: at(45_000),
        level: 'Error',
        event: 'http.request.failed',
        subsystem: 'backend',
        operation: 'GET /api/tasks/grouped',
        correlationId: 'req-003',
        project,
        runId: 'run-002',
        duration: { ms: 1284 },
        status: 'Failed',
        error: {
          type: 'System.IO.IOException',
          message: 'workspace path is locked by another process',
          code: 'EBUSY',
          retryable: true,
        },
        tags: ['route:jobs'],
      },
      {
        schemaVersion: 1,
        timestamp: at(40_000),
        level: 'Error',
        event: 'http.request.failed',
        subsystem: 'backend',
        operation: 'GET /api/tasks/grouped',
        correlationId: 'req-004',
        project,
        runId: 'run-002',
        duration: { ms: 1602 },
        status: 'Failed',
        error: {
          type: 'System.IO.IOException',
          message: 'workspace path is locked by another process',
          code: 'EBUSY',
          retryable: true,
        },
        tags: ['route:jobs'],
      },
      {
        schemaVersion: 1,
        timestamp: at(30_000),
        level: 'Info',
        event: 'order.placed',
        subsystem: 'ingest',
        operation: 'OrderRouter.Dispatch',
        correlationId: 'order-7',
        project,
        duration: { ms: 12 },
        status: 'Ok',
        payload: { orderId: 7, total: 42.5 },
      },
      {
        schemaVersion: 1,
        timestamp: at(20_000),
        level: 'Info',
        event: 'payment.declined',
        subsystem: 'ingest',
        operation: 'PaymentGateway.Charge',
        correlationId: 'order-7',
        project,
        duration: { ms: 240 },
        status: 'Failed',
        error: { message: 'card declined by issuer', code: 'CARD_DECLINED', retryable: false },
      },
      {
        schemaVersion: 1,
        timestamp: at(8_000),
        level: 'Info',
        event: 'http.request.completed',
        subsystem: 'backend',
        operation: 'GET /api/tasks',
        correlationId: 'req-006',
        project,
        duration: { ms: 38 },
        status: 'Ok',
        tags: ['route:jobs'],
      },
      {
        schemaVersion: 1,
        timestamp: at(2_000),
        level: 'Info',
        event: 'render.first-paint',
        subsystem: 'frontend',
        operation: 'project-shell',
        project,
        duration: { ms: 287 },
        status: 'Ok',
      },
    ];

    const warnings: RuntimeEventParseWarning[] = [
      {
        sourcePath: `logs/runtime/${project}/2026-05-06.jsonl`,
        lineNumber: 17,
        reason: 'json parse: unexpected token',
        rawLine: '{"event":"order.placed","level":"Info"',
      },
    ];

    return { project, events, warnings };
  },
};
