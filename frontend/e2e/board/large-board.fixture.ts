import type { Page } from '@playwright/test';

// Shell requests observed while opening and navigating the generated board.
// Keep this list exact: a new request must fail until its fixture is reviewed.
const emptyShellPaths = new Set([
  '/api/auto-review/status',
  '/api/cli/claude/models',
  '/api/cli/codex/models',
  '/api/cli/gemini/models',
  '/api/cli/model-migrations',
  '/api/cli/model-routing/recommendation',
  '/api/clients',
  '/api/clients/',
  '/api/clients/local-default/defaults',
  '/api/crash-recovery/pending',
  '/api/deploy/cards',
  '/api/develop-states',
  '/api/git/summary',
  '/api/pipeline/accepted-integration-alert',
  '/api/projects/Agent%20Software%20Studio/batch-gate/state',
  '/api/projects/Agent%20Software%20Studio/deploy/lanes',
  '/api/projects/settings',
  '/api/runner/auto-review-queue',
  '/api/runner/orchestrator-feed',
  '/api/runner/queue-starvation',
  '/api/tags',
  '/api/v1/management/host-releases',
  '/api/v1/management/provider-refusals',
  '/api/v1/management/runner-infrastructure-failures',
]);

const projectName = 'Agent Software Studio';
const watchPath = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

export function syntheticBoard(size: number, crossLaneSource = false) {
  const now = '2026-11-05T09:00:00Z';
  const jobs = Array.from({ length: size }, (_, index) => {
    const id = `AGT-S${String(index).padStart(5, '0')}`;
    return {
      id,
      taskKey: `${watchPath}::${id}`,
      jobKey: `${watchPath}::${id}`,
      displayKey: id,
      title: index === size - 1 ? `Window target ${id}` : `Generated task ${id}`,
      taskType: index === size - 1 ? 'bug' : 'feature',
      state: '2-ready',
      order: index,
      agent: 'codex',
      createdAt: now,
      watchPath,
      projectName,
      folderPath: `${watchPath}/2-ready/${id}`,
      lastActivity: now,
      sessionName: null,
      model: null,
      cliType: 'codex',
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
      tags: [],
    };
  });
  const completed = crossLaneSource ? [{
    ...jobs[0], id: 'AGT-X00000', taskKey: `${watchPath}::AGT-X00000`,
    jobKey: `${watchPath}::AGT-X00000`, displayKey: 'AGT-X00000',
    title: 'Generated cross-lane source', state: '6-completed', order: 0,
    folderPath: `${watchPath}/6-completed/AGT-X00000`,
  }] : [];
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: jobs,
    progress: [], failedPickup: [], autoReview: [], humanReview: [],
    review: [], completed, archive: [],
  };
  return { jobs: [...jobs, ...completed], grouped };
}

export async function installSyntheticBoard(page: Page, size: number, crossLaneSource = false) {
  const { jobs, grouped } = syntheticBoard(size, crossLaneSource);
  // The generated board runs against a frontend-only server. A new API call
  // needs an explicit fixture so the benchmark cannot silently measure a
  // board with missing data or an error dialog.
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (emptyShellPaths.has(new URL(url).pathname)) return route.fulfill({ json: [] });
    void route.abort('failed');
    throw new Error(`Unexpected synthetic-board API request: ${url}`);
  });
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
    }));
  });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/v1/studio/runner/status', route => route.fulfill({ json: { projects: {} } }));
  await page.route('**/api/environment', route => route.fulfill({ json: {
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  } }));
  await page.route('**/api/workspaces/*/quota/admission', route => route.fulfill({ json: {
    workspaceId: 'WS-SYNTH',
    workspace: { projectId: null, project: null, status: 'ok', message: null,
      overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  } }));
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: {
    workspaceId: 'WS-SYNTH', clis: [], cost: null,
  } }));
  await page.route('**/api/v1/projects', route => route.fulfill({ json: [{
    id: 'PROJ-SYNTH', displayName: projectName, archived: false,
  }] }));
  await page.route('**/api/tasks/archive**', route => route.fulfill({ json: { items: [], total: 0 } }));
  await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({ json: { items: [] } }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/management/links', route => route.fulfill({ json: [] }));
  await page.route('**/api/watcher/stalls', route => route.fulfill({ json: [] }));
  await page.route('**/api/watch-paths**', route => route.fulfill({ json: [{
    name: projectName, path: watchPath, rootPath: watchPath, repositoryPath: watchPath,
  }] }));
  await page.route(/\/api\/v1\/workspaces(\?|$)/, route => route.fulfill({ json: [{
    id: 'WS-SYNTH', displayName: 'Generated board', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-11-05T09:00:00Z',
    projects: [{ id: 'PROJ-SYNTH', displayName: projectName, shortCode: 'SYN',
      workspaceId: 'WS-SYNTH', color: null, cliDefault: null, modelDefault: null,
      sortOrder: 0, storageLocation: watchPath, archived: false,
      createdAt: '2026-11-05T09:00:00Z', urls: [] }],
  }] }));
  await page.route(/\/api\/v1\/studio\/board/, route => route.fulfill({ json: grouped }));
  await page.route(/\/api\/tasks(\?|$)/, route => route.fulfill({ json: jobs }));
  await page.route(/\/api\/tasks\/AGT-S00829\/core(\?|$)/, route => route.fulfill({ json: {} }));
}
