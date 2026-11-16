import { describe, expect, it } from 'vitest';
import { urlProject } from './testing/server-checkout.fixtures';
import { serverCheckoutMissing, serverCheckoutView, type ServerCheckoutStatus } from './server-checkout.model';


function status(overrides: Partial<ServerCheckoutStatus>): ServerCheckoutStatus {
  return { projectId: 'PROJ-2', state: 'missing', step: null, repositoryUrl: 'https://github.com/example/library.git',
    repositoryPath: null, branch: null, error: null, ...overrides };
}

describe('server checkout view (AGT-3809)', () => {
  it('flags a URL project without a checkout as missing and actionable', () => {
    expect(serverCheckoutMissing(urlProject())).toBe(true);
    const view = serverCheckoutView(urlProject(), null)!;
    expect(view.state).toBe('missing');
    expect(view.tone).toBe('warning');
    expect(view.canStart).toBe(true);
    expect(view.detail).toContain('https://github.com/example/library.git');
  });

  it('says nothing for a project with a checkout, without a URL, or archived', () => {
    expect(serverCheckoutView(urlProject({ repositoryPath: '/srv/library' }), null)).toBeNull();
    expect(serverCheckoutView(urlProject({ repositoryUrl: null }), null)).toBeNull();
    expect(serverCheckoutView(urlProject({ archived: true }), null)).toBeNull();
    expect(serverCheckoutView(urlProject(), status({ state: 'present', repositoryPath: '/srv/library' }))).toBeNull();
  });

  it('shows progress, the resulting path, and a retry after a failure', () => {
    expect(serverCheckoutView(urlProject(), status({ state: 'running', step: 'checkout', branch: 'develop' }))?.detail)
      .toBe('Checking out the integration branch develop…');
    const ready = serverCheckoutView(urlProject(), status({ state: 'ready', repositoryPath: '/srv/checkouts/PROJ-2', branch: 'develop' }))!;
    expect(ready.path).toBe('/srv/checkouts/PROJ-2');
    expect(ready.detail).toContain('on develop');
    expect(ready.canStart).toBe(false);
    const failed = serverCheckoutView(urlProject(), status({ state: 'failed', error: 'Clone denied.' }))!;
    expect(failed.detail).toBe('Clone denied.');
    expect(failed.canStart).toBe(true);
  });
});
