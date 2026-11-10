import { randomBytes } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, type Page } from '@playwright/test';

/** The demo origin the first-run Git shim maps onto its scratch bare repository. */
export const FIRST_RUN_ORIGIN = 'https://github.com/agent-studio/first-run-e2e.git';

/**
 * Signs in the first-run Owner (bootstrapping it on a fresh stack), waits for
 * the coding runner, archives earlier projects and registers a fresh demo
 * project on `first-run-coding`. Shared by the specs of the `first-run`
 * profile, which run against one real backend and its real runner pair.
 */
export async function prepareFirstRunProject(page: Page, workspace: string, prefix: string): Promise<string> {
  const passwordFile = join(workspace, 'first-run-owner-password');
  const ownerPassword = existsSync(passwordFile)
    ? readFileSync(passwordFile, 'utf8')
    : randomBytes(24).toString('base64url') + 'Aa1!';
  if (!existsSync(passwordFile)) writeFileSync(passwordFile, ownerPassword, { mode: 0o600 });
  const auth = await page.request.get('/api/auth/status');
  expect(auth.ok()).toBeTruthy();
  const current = await auth.json() as { bootstrapRequired: boolean };
  const signedIn = await page.request.post(current.bootstrapRequired
    ? '/api/auth/bootstrap' : '/api/auth/login', {
    headers: { 'X-Client-Id': 'local-default' },
    data: current.bootstrapRequired
      ? { username: 'first-run-owner', displayName: 'First Run Owner', password: ownerPassword }
      : { username: 'first-run-owner', password: ownerPassword },
  });
  expect(signedIn.ok(), `${signedIn.status()}: ${await signedIn.text()}`).toBeTruthy();
  await expect.poll(async () => {
    const clients = await page.request.get('/api/clients');
    if (!clients.ok()) return false;
    const entries = await clients.json() as { id: string }[];
    return entries.some(entry => entry.id === 'first-run-coding');
  }, { timeout: 120_000 }).toBe(true);
  const projects = await page.request.get('/api/projects');
  expect(projects.ok()).toBeTruthy();
  const existing = await projects.json() as { id: string; workspaceId: string; archived: boolean }[];
  for (const project of existing.filter(entry => !entry.archived)) {
    const archived = await page.request.put(`/api/projects/${encodeURIComponent(project.id)}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: { archived: true },
    });
    expect(archived.ok()).toBeTruthy();
  }
  const projectName = `${prefix} ${randomBytes(4).toString('hex')}`;
  const registered = await page.request.post('/api/projects', {
    headers: { 'X-Client-Id': 'local-default' },
    data: { workspaceId: existing[0]?.workspaceId ?? 'ws-default',
      displayName: projectName, repositoryUrl: FIRST_RUN_ORIGIN, executionRunner: 'first-run-coding' },
  });
  expect(registered.ok(), `${registered.status()}: ${await registered.text()}`).toBeTruthy();
  const project = await registered.json() as { id: string; repositoryPath?: string };
  expect(project.repositoryPath).toBeFalsy();
  return projectName;
}

/** Walks the first-run checklist's Git token step until the host verified push access. */
export async function verifyFirstRunGit(page: Page): Promise<void> {
  await page.getByTestId('first-run-git-method').selectOption('token');
  await page.getByTestId('first-run-git-token').fill(['test', 'x'.repeat(36)].join('-'));
  await page.getByTestId('first-run-git-start').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('awaiting-verify');
  await page.getByTestId('first-run-git-verify').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('verified', { timeout: 120_000 });
}

/** Starts the checklist's proof card and returns its card id. */
export async function createFirstRunProofCard(page: Page): Promise<string> {
  await page.getByTestId('first-run-demo-confirm').check();
  await page.getByTestId('first-run-create-proof').click();
  const proof = page.getByTestId('first-run-proof-id');
  await expect(proof).toBeVisible({ timeout: 120_000 });
  const id = /Card\s+(\S+)/.exec(await proof.innerText())?.[1];
  expect(id, 'proof card id').toBeTruthy();
  return id!;
}
