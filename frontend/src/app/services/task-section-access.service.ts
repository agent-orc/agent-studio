import { Injectable, computed, inject } from '@angular/core';
import { AuthSessionState } from './auth.service';

/**
 * Optional task-detail sections a Product session cannot read. Mirrors the
 * task-detail entries of `DeniedSegments` in
 * `backend/Features/Security/ContributorAccessPolicy.cs`; the backend
 * `ContributorAccessTests` guard fails when the two lists drift apart.
 */
export const PRODUCT_DENIED_TASK_SECTIONS = [
  'usage', 'output', 'session-events', 'logs', 'raw-logs', 'trace', 'agent-work-detail', 'conversation',
] as const;

/**
 * Which optional task-detail sections the signed-in session may request
 * (AGT-3669). A Product session opens cards without asking for run
 * internals the server refuses, so a normal card open sends no request
 * that is answered 403. The server stays the boundary; this only avoids
 * requests that are known to be refused.
 */
@Injectable({ providedIn: 'root' })
export class TaskSectionAccessService {
  private readonly auth = inject(AuthSessionState);

  readonly productSession = computed(() => {
    const status = this.auth.status();
    return status?.profile === 'networked' && status.authenticated === true
      && status.user?.role === 'contributor';
  });

  readonly readOnlySession = computed(() => {
    const status = this.auth.status();
    return status?.profile === 'public-demo-readonly'
      || (status?.profile === 'networked' && status.user?.role === 'viewer');
  });

  /** `section` is a task route segment: `output`, or a `/details/{resource}` name. */
  canRead(section: string): boolean {
    return !this.productSession()
      || !(PRODUCT_DENIED_TASK_SECTIONS as readonly string[]).includes(section);
  }
}
