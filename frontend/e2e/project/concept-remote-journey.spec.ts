import { expect, test, type Page } from '@playwright/test';
import { execFileSync, spawn, type ChildProcess } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { prepareFirstRunProject, verifyFirstRunGit } from '../helpers/first-run-setup';
import { setTheme } from '../helpers/theme';

test.use({ trace: 'off', video: 'off', reducedMotion: 'reduce' });

test('a remote concept reaches a reviewed Dossier, owner choices, promotion and completion', async ({ page }) => {
  test.setTimeout(30 * 60_000);
  test.skip(process.env.PW_E2E_PROFILE !== 'concept-remote', 'Dedicated disposable concept profile.');
  const workspace = process.env.PW_E2E_WORKSPACE!;
  expect(existsSync(join(workspace, 'bin', 'concept-codex'))).toBe(true);
  const projectName = await prepareFirstRunProject(page, workspace, 'Remote Concept');
  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-git-setup')).toBeVisible({ timeout: 120_000 });
  await verifyFirstRunGit(page);
  const projects = await json<{ id: string; displayName: string }[]>(page, '/api/projects');
  const project = projects.find(item => item.displayName === projectName);
  expect(project).toBeTruthy();
  let gate: ChildProcess | undefined;
  try {
    gate = await startPullGate(page, workspace);
    const enabled = await page.request.put(`/api/projects/${encodeURIComponent(projectName)}/batch-gate`, {
      headers: { 'X-Client-Id': 'local-default' },
      data: { enabled: true, scope: 'code', closeSize: 1, maximumSize: 1, minimumSize: 1,
        pressureSize: 1, deadlineMinutes: 1, gateTimeoutMinutes: 10 },
    });
    expect(enabled.ok(), `${enabled.status()}: ${await enabled.text()}`).toBe(true);
    const testSelection = await page.request.put(
      `/api/projects/${encodeURIComponent(projectName)}/test-execution`, {
        headers: { 'X-Client-Id': 'local-default' },
        data: { impactRules: [{ pathPrefixes: ['src/'],
          testCommands: ['./scripts/verify-implementation.sh'],
          reason: 'The implementation slice changes the gate probe.' }] },
      });
    expect(testSelection.ok(), `${testSelection.status()}: ${await testSelection.text()}`).toBe(true);
    const created = await page.request.post('/api/tasks', {
      headers: { 'X-Client-Id': 'local-default' },
      data: { title: 'Disposable remote concept', project: project!.id, mode: 'concept',
        agent: 'codex', cliType: 'codex',
        targetState: '1-preparation', promptMarkdown: 'Write a Dossier with three choices and two implementation slices.' },
    });
    expect(created.ok(), `${created.status()}: ${await created.text()}`).toBe(true);
    const body = await created.json() as { id: string; key?: string };
    const taskId = body.id;
    const detail = await json<{ info: { key?: string; id: string; watchPath: string; folderPath: string } }>(page,
      `/api/tasks/${taskId}?project=${encodeURIComponent(projectName)}`);
    const key = detail.info.key ?? detail.info.id;
    writeFileSync(join(workspace, 'concept-task-key'), key + '\n');
    const query = `?project=${encodeURIComponent(projectName)}`;
    const ready = await page.request.post(`/api/tasks/${taskId}/move${query}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: { targetState: '2-ready' },
    });
    expect(ready.ok(), `${ready.status()}: ${await ready.text()}`).toBe(true);
    const started = await page.request.post(`/api/tasks/${taskId}/start${query}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: {},
    });
    expect(started.ok(), `${started.status()}: ${await started.text()}`).toBe(true);
    await waitForState(page, taskId, projectName, '5-human-review');
    const initial = await deliveryEvidence(page, taskId, projectName);
    expect(initial.contentReview.requiredAspect).toBe('concept-fit');
    expect(initial.contentReview.contentStatus).toBe('pass');
    expect(initial.contentReview.reviewBriefVersion).toBe(initial.contentReview.currentBriefVersion);
    await openCard(page, taskId, detail.info.watchPath, 'initial');

    const continued = await page.request.post(`/api/tasks/${taskId}/continue${query}`, {
      headers: { 'X-Client-Id': 'local-default' },
      data: { mode: 'steer', prompt: 'Revise the Dossier and keep all three decisions.', reason: 'Owner follow-up round.' },
    });
    expect(continued.ok(), `${continued.status()}: ${await continued.text()}`).toBe(true);
    await expect.poll(() => Number(readFileSync(join(workspace, 'concept-round'), 'utf8')),
      { timeout: 8 * 60_000, intervals: [2_000] }).toBe(2);
    await waitForState(page, taskId, projectName, '5-human-review');
    const followUp = await deliveryEvidence(page, taskId, projectName);
    expect(followUp.contentReview.contentStatus).toBe('pass');
    expect(followUp.contentReview.reviewBriefVersion).toBe(followUp.contentReview.currentBriefVersion);
    expect(followUp.contentReview.currentBriefVersion).toBe(initial.contentReview.currentBriefVersion);
    await openCard(page, taskId, detail.info.watchPath, 'follow-up');
    const current = await json<{ info: { folderPath: string } }>(page,
      `/api/tasks/${taskId}?project=${encodeURIComponent(projectName)}`);

    const dossier = await json<{ workbench: { key: string; openDecisionCount: number }; html: string;
      fingerprint: string }>(page,
      `/api/projects/${encodeURIComponent(projectName)}/workbenches/remote-concept`);
    expect(dossier.workbench.openDecisionCount).toBe(3);
    expect(dossier.workbench.key).toMatch(/-W\d+$/);
    expect(dossier.html).toContain('Brief round 2');
    expect(dossier.html).toContain('data-decision-id="storage"');
    const conceptRecord = JSON.parse(readFileSync(join(current.info.folderPath,
      '.metadata', 'concept-workbench.json'), 'utf8')) as { commitSha: string };
    expect(conceptRecord.commitSha).toMatch(/^[a-f0-9]{40}$/);
    const keyedDescriptor = JSON.parse(execFileSync('git', ['--git-dir',
      join(workspace, 'first-run-origin.git'), 'show',
      `${conceptRecord.commitSha}:docs/remote-concept/workbench.json`], { encoding: 'utf8' })) as { key: string };
    expect(keyedDescriptor.key).toBe(dossier.workbench.key);
    const reviewVerification = JSON.parse(readFileSync(join(current.info.folderPath,
      'logs', 'review-verification.json'), 'utf8')) as { outcome: string; resultSha: string };
    expect(reviewVerification.outcome.toLowerCase()).toBe('pass');
    expect(reviewVerification.resultSha).toMatch(/^[a-f0-9]{40}$/);
    const decision = {
      operationId: 'concept-e2e-owner-choice', outcome: 'feature-spawn',
      expectedFingerprint: dossier.fingerprint, actor: 'Playwright Owner',
      task: { title: 'Apply the remote concept', goal: 'Implement the two bounded slices.',
        acceptanceCriteria: ['The Dossier choices are recorded.'], evidenceLinks: [] },
      responses: ['storage', 'review', 'gate'].map(decisionId => ({
        decisionId, kind: 'single', selectedOptionIds: [`${decisionId}-a`],
      })),
    };
    const decisionUrl = `/api/projects/${encodeURIComponent(projectName)}/workbenches/remote-concept/decisions`;
    const prepared = await page.request.post(`${decisionUrl}/prepare`, {
      headers: { 'X-Client-Id': 'local-default' }, data: decision,
    });
    expect(prepared.ok(), `${prepared.status()}: ${await prepared.text()}`).toBe(true);
    expect(((await prepared.json()) as { success: boolean }).success).toBe(true);
    const confirmed = await page.request.post(`${decisionUrl}/confirm`, {
      headers: { 'X-Client-Id': 'local-default' }, data: { ...decision, confirmed: true },
    });
    expect(confirmed.ok(), `${confirmed.status()}: ${await confirmed.text()}`).toBe(true);
    expect(((await confirmed.json()) as { success: boolean }).success).toBe(true);
    await openCard(page, taskId, detail.info.watchPath, 'owner-decisions');

    const plan = await json<{ source: { decisions: unknown[] }; items: unknown[] }>(page,
      `/api/tasks/${taskId}/promote-concept${query}`);
    expect(plan.source.decisions).toHaveLength(6);
    expect(plan.source.decisions.filter(item => (item as { operatorSelected: boolean }).operatorSelected)).toHaveLength(3);
    expect(plan.items).toHaveLength(2);
    const promoted = await page.request.post(`/api/tasks/${taskId}/promote-concept${query}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: {}, timeout: 120_000,
    });
    expect(promoted.ok(), `${promoted.status()}: ${await promoted.text()}`).toBe(true);
    const promotion = await promoted.json() as { created: { jobId: string; taskKey: string }[] };
    expect(promotion.created).toHaveLength(2);
    for (const [index, card] of promotion.created.entries()) {
      const related = await json<{ info: { dossier: { path: string; implementationIndex: number };
        references: { relatedTo: string[] } } }>(page,
        `/api/tasks/${card.jobId}?project=${encodeURIComponent(projectName)}`);
      expect(related.info.dossier.path).toBe('docs/remote-concept/index.html');
      expect(related.info.dossier.implementationIndex).toBe(index);
      expect(related.info.references.relatedTo).toContain(key);
    }
    await openCard(page, taskId, detail.info.watchPath, 'promoted');
    await waitForState(page, taskId, projectName, '6-completed');
    const completedCard = await json<{ info: { folderPath: string } }>(page,
      `/api/tasks/${taskId}?project=${encodeURIComponent(projectName)}`);
    const status = readFileSync(join(completedCard.info.folderPath, 'status.md'), 'utf8');
    expect(status).toContain('## What Was Done');
    expect(status).not.toContain('agent-studio:result-scaffold');
    await openCard(page, taskId, detail.info.watchPath, 'completed');

    const implementation = promotion.created[0];
    writeFileSync(join(workspace, 'concept-implementation-ready'), implementation.taskKey + '\n');
    const cliType = await page.request.put(`/api/tasks/${implementation.jobId}/cli-type${query}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: { cliType: 'codex' },
    });
    expect(cliType.ok(), `${cliType.status()}: ${await cliType.text()}`).toBe(true);
    const implementationReady = await page.request.post(`/api/tasks/${implementation.jobId}/move${query}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: { targetState: '2-ready' },
    });
    expect(implementationReady.ok(), `${implementationReady.status()}: ${await implementationReady.text()}`).toBe(true);
    const implementationStarted = await page.request.post(`/api/tasks/${implementation.jobId}/start${query}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: {},
    });
    expect(implementationStarted.ok(), `${implementationStarted.status()}: ${await implementationStarted.text()}`).toBe(true);
    await expect.poll(() => existsSync(join(workspace, 'concept-implementation-run')),
      { timeout: 8 * 60_000, intervals: [2_000] }).toBe(true);
    const gateStateUrl = `/api/projects/${encodeURIComponent(projectName)}/batch-gate/state`;
    type GateBatch = { phase: string; members: { taskKey: string }[];
      runs: { outcome: string; evidenceAvailable: boolean }[];
      publication: { testedCandidateSha: string; verifiedRemoteSha: string } | null };
    await expect.poll(async () => {
      const state = await json<{ batches: GateBatch[] }>(page, gateStateUrl);
      return state.batches.find(batch => batch.members.some(member => member.taskKey === implementation.taskKey)
        && batch.phase === 'published') ?? null;
    }, { timeout: 8 * 60_000, intervals: [2_000] }).toBeTruthy();
    const gateState = await json<{ batches: GateBatch[] }>(page, gateStateUrl);
    const gateBatch = gateState.batches.find(batch => batch.members.some(member => member.taskKey === implementation.taskKey));
    expect(gateBatch?.runs.some(run => run.outcome === 'pass' && run.evidenceAvailable)).toBe(true);
    expect(gateBatch?.publication?.testedCandidateSha).toMatch(/^[a-f0-9]{40}$/);
    expect(gateBatch?.publication?.verifiedRemoteSha).toBe(gateBatch?.publication?.testedCandidateSha);
    await waitForState(page, implementation.jobId, projectName, '5-human-review');
    const gateCard = await json<{ info: { watchPath: string; folderPath: string } }>(page,
      `/api/tasks/${implementation.jobId}?project=${encodeURIComponent(projectName)}`);
    await openCard(page, implementation.jobId, gateCard.info.watchPath, 'gate-published');
    if (process.env.JOB_RESULTS_DIR) {
      writeFileSync(join(process.env.JOB_RESULTS_DIR, 'concept-evidence.json'), JSON.stringify({
        project: projectName,
        conceptTaskKey: key,
        initialBriefVersion: initial.contentReview.currentBriefVersion,
        followUpBriefVersion: followUp.contentReview.currentBriefVersion,
        dossierKey: dossier.workbench.key,
        documentKeyIntegrationSha: conceptRecord.commitSha,
        reviewResultSha: reviewVerification.resultSha,
        ownerDecisions: decision.responses,
        promotedCards: promotion.created,
        batchPublication: gateBatch?.publication,
      }, null, 2) + '\n');
    }
  } finally {
    if (gate && gate.exitCode === null) {
      gate.kill('SIGTERM');
      await new Promise<void>(resolve => {
        if (gate.exitCode !== null) return resolve();
        const deadline = setTimeout(() => { gate.kill('SIGKILL'); resolve(); }, 5_000);
        gate.once('exit', () => { clearTimeout(deadline); resolve(); });
      });
    }
  }
});

async function json<T>(page: Page, url: string): Promise<T> {
  const response = await page.request.get(url);
  expect(response.ok(), `${response.status()}: ${await response.text()}`).toBe(true);
  return response.json() as Promise<T>;
}

async function waitForState(page: Page, id: string, project: string, state: string): Promise<void> {
  await expect.poll(async () => {
    const response = await page.request.get(`/api/tasks/${id}?project=${encodeURIComponent(project)}`);
    if (!response.ok()) return `HTTP ${response.status()}`;
    return ((await response.json()) as { info: { state: string } }).info.state;
  }, { timeout: 8 * 60_000, intervals: [2_000] }).toBe(state);
}

interface DeliveryEvidence {
  contentReview: {
    requiredAspect: string;
    contentStatus: string;
    currentBriefVersion: string;
    reviewBriefVersion: string;
  };
}

async function deliveryEvidence(page: Page, id: string, project: string): Promise<DeliveryEvidence> {
  return json<DeliveryEvidence>(page,
    `/api/tasks/${id}/delivery-claim?project=${encodeURIComponent(project)}`);
}

async function startPullGate(page: Page, workspace: string): Promise<ChildProcess> {
  const enrollment = await page.request.post('/api/auth/runner-enrollments', {
    headers: { 'X-Client-Id': 'local-default' },
    data: { name: 'concept-e2e-gate', scopes: ['gate.execute'] },
  });
  expect(enrollment.ok(), `${enrollment.status()}: ${await enrollment.text()}`).toBe(true);
  const { enrollmentCode } = await enrollment.json() as { enrollmentCode: string };
  const issued = await page.request.post('/api/auth/runner-enroll', {
    headers: { 'X-Client-Id': 'local-default' }, data: { code: enrollmentCode },
  });
  expect(issued.ok(), `${issued.status()}: ${await issued.text()}`).toBe(true);
  const { secret } = await issued.json() as { secret: string };
  const log = join(workspace, 'pull-gate.log');
  writeFileSync(log, '');
  const worker = spawn('dotnet', [join(process.cwd(), '..', 'backend', 'bin', 'e2e-publish', 'OrchestratorApi.dll'),
    '--pull-build-test-gate-worker', process.env.PW_BACKEND_URL!], {
    env: { ...process.env, GATE_WORKER_TOKEN: secret, GATE_WORKER_NAME: 'concept-e2e-gate' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  worker.stdout?.on('data', chunk => writeFileSync(log, chunk, { flag: 'a' }));
  worker.stderr?.on('data', chunk => writeFileSync(log, chunk, { flag: 'a' }));
  await expect.poll(async () => {
    if (worker.exitCode !== null) throw new Error(`Pull gate exited: ${readFileSync(log, 'utf8')}`);
    const response = await page.request.get('/api/system/execution-infrastructure');
    if (!response.ok()) return 'unavailable';
    return ((await response.json()) as { gate: { transport: string } }).gate.transport;
  }, { timeout: 30_000, intervals: [1_000] }).toBe('pull');
  writeFileSync(join(workspace, 'concept-gate-ready'), 'started\n');
  return worker;
}

async function openCard(page: Page, id: string, watchPath: string, stage: string): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  await setTheme(page, 'light');
  await expect(page.getByTestId('prompt-tab-overview')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('result-view')).toBeVisible({ timeout: 30_000 });
  await dismissNotifications(page);
  const root = process.env.JOB_RESULTS_DIR;
  if (!root) return;
  mkdirSync(root, { recursive: true });
  await page.screenshot({ path: join(root, `concept-${stage}-light-1280.png`) });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByTestId('detail-mobile-pane-result').click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible();
  await expect(page.getByTestId('result-view')).toBeVisible();
  await dismissNotifications(page);
  await page.mouse.move(360, 700);
  await page.screenshot({ path: join(root, `concept-${stage}-light-390.png`) });
  await setTheme(page, 'dark');
  await expect(page.getByTestId('result-view')).toBeVisible();
  await dismissNotifications(page);
  await page.mouse.move(360, 700);
  await page.screenshot({ path: join(root, `concept-${stage}-dark-390.png`) });
}

async function dismissNotifications(page: Page): Promise<void> {
  const close = page.getByTestId('notification-close');
  while (await close.count()) await close.first().click();
}
