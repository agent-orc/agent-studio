// The documented Studio routes the product scenario uses, in one place.
// Invitations, users and production approval follow the Product role
// (AGT-3182, AccessSecurityEndpoints and IamEndpoints); the card preview is
// the `preview` field of a task (AGT-3180, TaskPreview); preview feedback and
// before and after screenshots follow AGT-3181 (TaskRunnerEndpoints,
// TaskFilesEndpoints); stage and production follow AGT-3176.
import { OperationError } from '../../../tools/studio-op/src/studio.mjs';

const enc = encodeURIComponent;
const WARMING_RETRIES = 20;
// A chat turn answers when the orchestrator has replied (OrchestratorRunner, 5 minutes).
const CHAT_TIMEOUT_MS = 6 * 60_000;

/** Routes a person with the Product role must not reach (ContributorAccessPolicy). */
export const REFUSED_READS = Object.freeze([
  Object.freeze({ id: 'credential-inventory', action: 'Open the credential inventory', path: '/api/v1/management/credential-inventory' }),
  Object.freeze({ id: 'secrets', action: 'List the stored secrets', path: '/api/secrets' }),
  Object.freeze({ id: 'remote-hosts', action: 'Open the execution host settings', path: '/api/v1/management/remote-hosts' }),
  Object.freeze({ id: 'project-settings', action: 'Open the project settings', path: '/api/projects/settings' }),
]);

export class ProductApi {
  constructor(session, { sleep = ms => new Promise(resolve => setTimeout(resolve, ms)) } = {}) {
    this.session = session;
    this.sleep = sleep;
  }

  signIn(credentials) { return this.session.signIn(credentials); }

  // People: invitations, users, bindings (Owner)
  invite(body) { return this.session.post('/api/auth/invitations', body); }
  users() { return this.session.get('/api/auth/users'); }
  updateUser(id, body) { return this.session.put(`/api/auth/users/${enc(id)}`, body); }
  iam() { return this.session.get('/api/auth/iam'); }
  deleteBinding(id) { return this.session.delete(`/api/auth/iam/bindings/${enc(id)}`); }

  // Workspaces, projects, Project URLs
  workspaces() { return this.session.get('/api/workspaces'); }
  projects() { return this.session.get('/api/projects'); }
  workspaceQuota(workspaceId) { return this.session.get(`/api/workspaces/${enc(workspaceId)}/quota`); }
  addProjectUrl(projectId, label, url) { return this.session.post(`/api/projects/${enc(projectId)}/urls`, { label, url }); }
  removeProjectUrl(projectId, urlId) { return this.session.delete(`/api/projects/${enc(projectId)}/urls/${enc(urlId)}`); }

  // The project chat beside the preview; "URL preview" switches it to product language.
  productChat(projectId, text) {
    return this.session.send('POST', `/api/runner/project:${enc(projectId)}/orchestrator-chat`, {
      body: { text, navigationContext: { observedSurface: 'URL preview', currentPage: 'url-preview' } },
      timeoutMs: CHAT_TIMEOUT_MS,
    });
  }

  // Cards
  tasks(project) { return this.session.get('/api/tasks/', { project }); }
  screenshots(id, project) { return this.session.get(`/api/tasks/${enc(id)}/screenshots`, { project }); }
  /** The card prompt as text; a continue such as preview feedback appends its next-round instruction. */
  promptFile(id, project) { return this.session.send('GET', `/api/tasks/${enc(id)}/files/prompt.md`, { query: { project }, raw: true }); }

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
  /** Answers { status, body } so a refusal is evidence, not an exception. */
  approvePromotion(projectName, artifact) {
    return this.session.send('POST', `/api/projects/${enc(projectName)}/environments/promotions/approve`, { body: { artifact } });
  }

  /** Tries a read the Product role must not reach; answers { status, body }. */
  tryRead(path) { return this.session.send('GET', path); }
}
