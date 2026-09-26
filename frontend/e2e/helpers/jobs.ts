import { api } from './api';

/**
 * Job DTO subset we rely on in tests. Keep narrow on purpose so a backend
 * field rename doesn't ripple through every spec.
 *
 * The backend addresses jobs by `jobId` + `watchPath` (project root), not by
 * the composite `jobKey`. All write endpoints accept `?watchPath=...`.
 */
export interface JobExecution {
  jobId: string;
  jobKey: string;
  processId: number;
  startedAt: string;
  status: 'running' | 'completed' | 'failed' | 'stopped' | 'cancelled' | string;
  exitCode: number | null;
  durationSeconds: number | null;
  model?: string | null;
}

export interface Job {
  id: string;
  jobKey: string;
  title: string;
  state: string;
  agent: string | null;
  cliType: string | null;
  model: string | null;
  watchPath: string;
  projectName: string;
  folderPath: string;
  execution: JobExecution | null;
}

export const listJobs = () => api<Job[]>('/api/tasks');

interface JobDetail {
  info: Job;
  promptMarkdown: string | null;
  statusMarkdown: string | null;
  log?: unknown[];
}

/** GET /api/tasks/{id} returns a JobDetail; we unwrap to the Job for callers. */
export async function getJob(jobId: string, watchPath?: string): Promise<Job> {
  const qs = watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '';
  const detail = await api<JobDetail>(`/api/tasks/${encodeURIComponent(jobId)}${qs}`);
  return detail.info;
}

export async function getJobDetail(jobId: string, watchPath?: string): Promise<JobDetail> {
  const qs = watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '';
  return api<JobDetail>(`/api/tasks/${encodeURIComponent(jobId)}${qs}`);
}

export interface CreateJobInput {
  id?: string;
  title: string;
  watchPath: string;
  agent?: string;       // 'claude' | 'copilot' | 'codex'
  cliType?: string;     // 'claude' | 'copilot' | 'codex'
  model?: string;
  promptMarkdown?: string;
  targetState?: string; // default '1-preparation'; we usually want '2-ready'
  mode?: string;
  requiresIntegration?: boolean;
  /**
   * When true, the new job is marked as a Playwright / E2E fixture
   * (`fixture: true` in job.json) and is hidden from the default
   * `/api/tasks` and `/api/tasks/grouped` responses on stable. Tests can
   * still observe their own fixtures via `?includeFixtures=true`.
   * Defaults to `true` so spec authors do not have to remember the flag;
   * pass `false` only when a spec genuinely needs to plant a "real" job.
   */
  fixture?: boolean;
}

export async function createJob(input: CreateJobInput): Promise<{ id: string }> {
  return api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id ?? '',
      title: input.title,
      watchPath: input.watchPath,
      agent: input.agent ?? 'claude',
      cliType: input.cliType ?? 'claude',
      model: input.model ?? null,
      promptMarkdown: input.promptMarkdown ?? null,
      targetState: input.targetState ?? '2-ready',
      mode: input.mode,
      requiresIntegration: input.requiresIntegration,
      fixture: input.fixture ?? true
    })
  });
}

export interface StartJobOptions {
  model?: string;
  cliType?: string;
}

export async function startJob(
  jobId: string,
  watchPath: string,
  opts: StartJobOptions = {}
): Promise<JobExecution> {
  // /start returns ContinueJobResponse: { status: "started", execution }
  // when running now, or { status: "queued", queued: { ... } } when the
  // project is busy. Tests that call this helper expect the running
  // case; unwrap to the execution and throw if we got queued instead.
  const resp = await api<{ status: string; execution?: JobExecution; queued?: unknown }>(
    `/api/tasks/${encodeURIComponent(jobId)}/start?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'POST', body: JSON.stringify({ model: opts.model ?? null, cliType: opts.cliType ?? null }) }
  );
  if (resp.status !== 'started' || !resp.execution) {
    throw new Error(
      `startJob(${jobId}): expected status=started, got status=${resp.status}. queued=${JSON.stringify(resp.queued ?? null)}`
    );
  }
  return resp.execution;
}

export async function getJobOutput(jobId: string, watchPath: string): Promise<unknown> {
  return api(
    `/api/tasks/${encodeURIComponent(jobId)}/output?watchPath=${encodeURIComponent(watchPath)}`
  );
}

export async function moveJob(jobId: string, watchPath: string, targetState: string): Promise<void> {
  await api(
    `/api/tasks/${encodeURIComponent(jobId)}/move?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'POST', body: JSON.stringify({ targetState }) }
  );
}

/**
 * Polls a job until `predicate` returns true or timeout expires.
 * Returns the latest snapshot regardless. Throws only if no snapshot ever
 * arrives (network error every poll).
 */
export async function waitForJob(
  jobId: string,
  watchPath: string,
  predicate: (j: Job) => boolean,
  { timeoutMs = 180_000, intervalMs = 2_000 }: { timeoutMs?: number; intervalMs?: number } = {}
): Promise<Job> {
  const start = Date.now();
  let last: Job | null = null;
  while (Date.now() - start < timeoutMs) {
    try {
      last = await getJob(jobId, watchPath);
      if (predicate(last)) return last;
    } catch {
      // tolerate transient errors during job churn
    }
    await new Promise(r => setTimeout(r, intervalMs));
  }
  if (!last) throw new Error(`Job ${jobId} never returned a snapshot in ${timeoutMs}ms`);
  return last;
}
