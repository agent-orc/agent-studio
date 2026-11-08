// The documented Studio routes the lifecycle scenario uses, in one place.
// Routes and field names follow the companion cards: project from template
// (AGT-3175, RegistryEndpoints), stage and production (AGT-3176,
// ProjectOperatorDashboardEndpoints), quotas (AGT-3177, WorkspaceQuotaEndpoints)
// and the project orchestrator chat (RunnerEndpoints orchestrator-chat).
import { OperationError } from '../../../tools/studio-op/src/studio.mjs';

const enc = encodeURIComponent;
const WARMING_RETRIES = 20;
// A chat turn answers when the orchestrator has replied; that can take minutes.
const CHAT_TIMEOUT_MS = 15 * 60_000;

export class LifecycleApi {
  constructor(session, { sleep = ms => new Promise(resolve => setTimeout(resolve, ms)) } = {}) {
    this.session = session;
    this.sleep = sleep;
  }

  signIn(credentials) { return this.session.signIn(credentials); }
  authStatus() { return this.session.get('/api/auth/status'); }

  // Workspaces and projects
  workspaces() { return this.session.get('/api/workspaces'); }
  createWorkspace(displayName) { return this.session.post('/api/workspaces', { displayName }); }
  deleteWorkspace(id) { return this.session.delete(`/api/workspaces/${enc(id)}`); }
  projects() { return this.session.get('/api/projects'); }
  createProject(body) { return this.session.post('/api/projects', body); }
  deleteProject(projectId) { return this.session.delete(`/api/projects/${enc(projectId)}`); }

  // Quotas: limits are an Owner session action.
  workspaceQuota(workspaceId) { return this.session.get(`/api/workspaces/${enc(workspaceId)}/quota`); }
  setWorkspaceQuota(workspaceId, limits) { return this.session.put(`/api/workspaces/${enc(workspaceId)}/quota`, limits); }
  quotaAdmission(projectName) { return this.session.get(`/api/quota/admission/${enc(projectName)}`); }

  // Orchestrator chat of one project, the route the Studio chat panel uses
  chat(projectName, text) {
    return this.session.request('POST', `/api/runner/${enc(projectName)}/orchestrator-chat`,
      { body: { text }, timeoutMs: CHAT_TIMEOUT_MS });
  }

  // Cards
  tasks(project) { return this.session.get('/api/tasks/', { project }); }
  archivedTasks(project) { return this.session.get('/api/tasks/archive', { project, limit: 200 }); }
  createTask(body) { return this.session.post('/api/tasks/', body); }
  reviewProjection(id, project) { return this.session.get(`/api/tasks/${enc(id)}/review-projection`, { project }); }

  /** Reads one card; repeats while the index is warming after a create. */
  async task(id, project) {
    for (let attempt = 0; ; attempt++) {
      const answer = await this.session.send('GET', `/api/tasks/${enc(id)}`, { query: { project } });
      if (answer.status === 202 && answer.body?.reason === 'task-index-warming' && attempt < WARMING_RETRIES) {
        await this.sleep(500);
        continue;
      }
      if (answer.status >= 200 && answer.status < 300) return answer.body?.info ?? answer.body;
      throw new OperationError(answer.body?.error ?? `http-${answer.status}`,
        `GET /api/tasks/${id} answered ${answer.status}`, { status: answer.status, body: answer.body });
    }
  }

  /** Moves a card the way studio-op does: take the attention lease, send its revision. */
  async moveTask(id, project, targetState) {
    const attention = await this.session.request('POST', `/api/tasks/${enc(id)}/attention`, { query: { project } });
    return this.session.request('POST', `/api/tasks/${enc(id)}/move`, {
      query: { project }, body: { targetState }, headers: { 'If-Match': attention.revision },
    });
  }

  // Stage and production
  environments(projectName) { return this.session.get(`/api/projects/${enc(projectName)}/environments`); }
  async releases(projectName) {
    const answer = await this.session.get(`/api/projects/${enc(projectName)}/deploy/releases`);
    return Array.isArray(answer) ? answer : answer?.items ?? answer?.releases ?? [];
  }
  stopEnvironment(projectName, environmentId) {
    return this.session.send('POST', `/api/projects/${enc(projectName)}/environments/${enc(environmentId)}/stop`, { body: {} });
  }
  approvePromotion(projectName, artifact) {
    return this.session.post(`/api/projects/${enc(projectName)}/environments/promotions/approve`, { artifact });
  }
}
