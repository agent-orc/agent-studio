import type { RegistryProjectSummary } from '../../../models/task.model';

/** A local URL project without a server checkout (AGT-3809). */
export function urlProject(overrides: Partial<RegistryProjectSummary> = {}): RegistryProjectSummary {
  return {
    sourceType: 'local-folder', id: 'PROJ-2', displayName: 'Library', shortCode: 'LIB', workspaceId: 'ws-default',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 1, storageLocation: '/store/projects/PROJ-2/tasks',
    repositoryPath: null, rootPath: null, repositoryUrl: 'https://github.com/example/library.git', urls: [],
    archived: false, createdAt: '2026-11-15T00:00:00Z', ...overrides,
  };
}
