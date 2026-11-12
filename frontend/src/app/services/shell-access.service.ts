import { computed, inject, Injectable } from '@angular/core';
import { AuthSessionState } from './auth.service';

/** The session's projected role is the server's effective binding summary. */
@Injectable({ providedIn: 'root' })
export class ShellAccessService {
  private readonly session = inject(AuthSessionState);
  /**
   * Product and read-only sessions have a board, but no operator shell. Only a
   * session whose role is known and is not owner or operator is limited; local,
   * unknown and role-less sessions keep the full shell and let the server decide.
   */
  readonly limited = computed(() => {
    const status = this.session.status();
    const role = status?.profile === 'networked' && status.authenticated ? status.user?.role : undefined;
    return !!role && role !== 'owner' && role !== 'operator';
  });

  /** The operator shell: every session the server has not narrowed to the board. */
  readonly operator = computed(() => !this.limited());

  /** Background reads allowed by ContributorAccessPolicy and the board contract. */
  canReadBackground(url: string): boolean {
    if (!this.limited()) return true;
    const path = url.split('?', 1)[0];
    if (path === '/api/auth/status' || path === '/api/auth/session'
        || path === '/api/auth/sessions') return true;
    if (path === '/api/v1/studio/auth/status' || path === '/api/v1/studio/board'
        || path === '/api/v1/projects') return true;
    if (path.startsWith('/api/v1/studio/auth/sessions')) return true;
    if (path.startsWith('/api/v1/studio/runner/') && path.endsWith('/orchestrator-chat')) return true;
    if (path.startsWith('/api/v1/studio/orchestrator/context/project:')) return true;
    if (path.startsWith('/api/v1/studio/projects/') && path.endsWith('/pipeline-health')) return true;
    if (path.startsWith('/api/v1/projects/')) {
      const parts = path.split('/');
      return (parts[5] === 'tasks' || parts[5] === 'board')
        && !parts.some(segment => PRODUCT_DENIED_SEGMENTS.has(segment));
    }
    if (path === '/api/projects' || path === '/api/workspaces' || path === '/api/watch-paths'
        || path === '/api/registry' || path === '/api/magazine') return true;
    if (path.startsWith('/api/registry/') || path.startsWith('/api/magazine/')) return true;
    if (path === '/api/tasks' || path.startsWith('/api/tasks/')) {
      return !path.split('/').some(segment => PRODUCT_DENIED_SEGMENTS.has(segment));
    }
    if (path.startsWith('/api/projects/')) {
      const section = path.split('/')[4];
      return !!section && PRODUCT_PROJECT_SECTIONS.has(section);
    }
    if (path.startsWith('/api/orchestrator/context/') || path.startsWith('/api/orchestrator/sessions/')) return true;
    if (path.startsWith('/api/runner/') && path.includes('/orchestrator-chat')) return true;
    return false;
  }
}

const PRODUCT_DENIED_SEGMENTS = new Set([
  'credentials', 'hosts', 'settings', 'quotas', 'security', 'tokens', 'usage',
  'logs', 'raw-logs', 'merges', 'promotions', 'secrets', 'integration',
  'output', 'trace', 'session-events', 'agent-work-detail', 'conversation',
  'checkout',
]);
const PRODUCT_PROJECT_SECTIONS = new Set([
  'chat', 'docs', 'visual-evidence', 'snapshot', 'throughput', 'cycle-time', 'completed-lane',
]);
