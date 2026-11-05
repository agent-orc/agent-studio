import type { Page } from '@playwright/test';

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
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
    }));
  });
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
}
