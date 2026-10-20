import { randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import type { Page, Route } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

const shots = resolve(process.env.JOB_RESULTS_DIR ?? '../results', 'secrets');
const json = (body: unknown, status = 200) => (route: Route) => route.fulfill({
  status, contentType: 'application/json', body: JSON.stringify(body),
});

interface FakeVersion { version: number; origin: string; active: boolean; createdAt: string; createdBy: string;
  revokedAt: string | null; revokedBy: string | null; lastUsedAt: string | null }
interface FakeSecret { scope: string; name: string; versions: FakeVersion[] }

/** A small stateful stand-in for the secret routes. It records request values but never returns one. */
function fakeSecrets() {
  const secrets: FakeSecret[] = [];
  const received: string[] = [];
  const at = '2026-10-19T09:00:00Z';
  const view = (secret: FakeSecret) => {
    const active = secret.versions.filter(item => item.active).map(item => item.version);
    return {
      reference: `secret://${secret.scope}/${secret.name}`, scope: secret.scope,
      scopeKind: secret.scope === 'workspace' ? 'workspace' : secret.scope.includes('/') ? 'environment' : 'project',
      name: secret.name, status: active.length ? 'active' : 'revoked',
      currentVersion: active.length ? Math.max(...active) : null,
      createdAt: at, createdBy: 'human:local-operator', updatedAt: at, updatedBy: 'human:local-operator',
      rotatedAt: secret.versions.length > 1 ? at : null, rotatedBy: secret.versions.length > 1 ? 'human:local-operator' : null,
      lastUsedAt: null, lastUsedBy: null, lastUsedPurpose: null,
      versions: [...secret.versions].reverse(),
    };
  };
  const version = (n: number, origin: string): FakeVersion => ({ version: n, origin, active: true, createdAt: at,
    createdBy: 'human:local-operator', revokedAt: null, revokedBy: null, lastUsedAt: null });
  /** Splits a scope route into its scope key and the `/NAME/action` rest. */
  const parse = (url: URL) => {
    if (url.pathname.startsWith('/api/secrets/workspace')) {
      return { scope: 'workspace', rest: url.pathname.slice('/api/secrets/workspace'.length) };
    }
    const match = /^\/api\/projects\/([^/]+)(?:\/environments\/([^/]+))?\/secrets/.exec(url.pathname)!;
    return { scope: match[2] ? `${match[1]}/${match[2]}` : match[1], rest: url.pathname.slice(match[0].length) };
  };
  const handle = async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const { scope, rest } = parse(url);
    const body = request.postDataJSON() as { name?: string; value?: string; version?: number | null } | null;
    if (body?.value) received.push(body.value);
    const name = rest.split('/')[1];
    if (request.method() === 'POST' && !name) {
      const secret: FakeSecret = { scope, name: body!.name!, versions: [version(1, 'put')] };
      secrets.push(secret);
      return json(view(secret), 201)(route);
    }
    const secret = secrets.find(item => item.scope === scope && item.name === name)!;
    if (url.pathname.endsWith('/rotate')) secret.versions.push(version(secret.versions.length + 1, 'rotate'));
    if (url.pathname.endsWith('/revoke')) {
      for (const item of secret.versions) {
        if (item.active && (body?.version == null || body.version === item.version)) {
          item.active = false; item.revokedAt = at; item.revokedBy = 'human:local-operator';
        }
      }
    }
    return json(view(secret))(route);
  };
  return { secrets, received, view, handle };
}

async function stub(page: Page, unreadable: readonly object[] = []) {
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/v1/studio/board', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([{ name: 'memowikis', path: '/work/memowikis', rootPath: '/work/memowikis' }]));
  await page.route('**/api/v1/studio/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 60_000, snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/v1/workspaces*', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/projects/memowikis/environments', json({ project: 'memowikis', environments: [
    { declaration: { id: 'stage' } }, { declaration: { id: 'production' } },
  ], promotions: [], jobs: [], failureCards: [] }));
  const fake = fakeSecrets();
  const providers: { scope: string; provider: string; address?: string; mount?: string; vault?: string;
    credential?: string; ttlSeconds: number }[] = [];
  await page.route('**/api/secrets/provider', route => {
    const assignment = route.request().postDataJSON() as typeof providers[number];
    providers.push(assignment);
    return json(assignment)(route);
  });
  await page.route('**/api/secrets', route => json({ store: 'builtin', keySource: 'key-file', canWriteWorkspace: true,
    secrets: fake.secrets.map(fake.view), unreadable, providers,
    health: providers.map(item => ({ scope: item.scope, provider: item.provider,
      state: item.provider === 'builtin' ? 'ready' : 'unavailable', reason: 'Check configuration.' })) })(route));
  await page.route('**/api/secrets/workspace**', fake.handle);
  await page.route('**/api/projects/memowikis/**/secrets**', fake.handle);
  await page.route('**/api/projects/memowikis/secrets**', fake.handle);
  return { ...fake, providers };
}

test('Secrets section adds, rotates and revokes without ever showing a value', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  const fake = await stub(page);
  await page.goto('/#/workspace/settings/secrets', { waitUntil: 'domcontentloaded' });
  const panel = page.getByTestId('secrets-panel');
  await expect(panel).toBeVisible();
  await expect(page.getByTestId('workspace-settings-rail-secrets')).toBeVisible();
  await expect(page.getByTestId('secrets-empty')).toBeVisible();

  const workspaceValue = `value-${randomUUID()}`;
  await page.getByTestId('secrets-add-name').fill('MAIL_KEY');
  await page.getByTestId('secrets-add-value').fill(workspaceValue);
  await expect(page.getByTestId('secrets-add-value')).toHaveAttribute('type', 'password');
  await page.getByTestId('secrets-add').getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByTestId('secrets-row')).toHaveCount(1);
  await expect(page.getByTestId('secrets-add-value')).toHaveValue('');
  await expect(panel).toContainText('secret://workspace/MAIL_KEY');

  await page.getByTestId('secrets-scope-project').selectOption('memowikis');
  await page.getByTestId('secrets-scope-environment').selectOption('stage');
  await expect(page.getByTestId('secrets-empty')).toBeVisible();
  const stageValue = `value-${randomUUID()}`;
  await page.getByTestId('secrets-add-name').fill('DATABASE_URL');
  await page.getByTestId('secrets-add-value').fill(stageValue);
  await page.getByTestId('secrets-add').getByRole('button', { name: 'Add', exact: true }).click();
  await expect(panel).toContainText('secret://memowikis/stage/DATABASE_URL');

  const rotatedValue = `value-${randomUUID()}`;
  await page.getByTestId('secrets-rotate').click();
  await page.getByTestId('secrets-rotate-value').fill(rotatedValue);
  await page.getByTestId('secrets-rotate-save').click();
  await expect(page.getByTestId('secrets-row').locator('.secrets__badge')).toHaveText('v2');
  await page.getByTestId('secrets-revoke-version').last().click();
  await expect(page.getByTestId('secrets-row').locator('.secrets__versions li[data-active="false"]')).toHaveCount(1);

  expect(fake.received).toEqual([workspaceValue, stageValue, rotatedValue]);
  const html = await page.content();
  for (const value of fake.received) expect(html).not.toContain(value);
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(shots, `secrets-${theme}--mocked.png`) });
  }
});

test('Secrets section warns about items the configured key cannot open and still lists the rest', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  await stub(page, [{ id: '4f1c', scope: 'memowikis', name: 'API_TOKEN', problem: 'key-unavailable', reason: 'sealed with another key' }]);
  await page.goto('/#/workspace/settings/secrets', { waitUntil: 'domcontentloaded' });
  const warning = page.getByTestId('secrets-unreadable');
  await expect(warning).toContainText('1 stored secret cannot be opened');
  await expect(warning).toContainText('secret://memowikis/API_TOKEN');
  await expect(warning).toContainText('key-unavailable');
  await page.getByTestId('secrets-add-name').fill('MAIL_KEY');
  await page.getByTestId('secrets-add-value').fill(`value-${randomUUID()}`);
  await page.getByTestId('secrets-add').getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByTestId('secrets-row')).toHaveCount(1);
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(shots, `secrets-unreadable-${theme}--mocked.png`) });
  }
});


test('Secrets section configures a provider per scope and shows reachability in both themes', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  const fake = await stub(page);
  await page.goto('/#/workspace/settings/secrets', { waitUntil: 'domcontentloaded' });
  await page.getByTestId('secrets-scope-project').selectOption('memowikis');
  await page.getByTestId('secrets-provider-edit').click();
  const form = page.getByTestId('secrets-provider-form');
  await form.getByLabel('Store').selectOption('vault');
  await form.getByLabel('Address').fill('https://vault.example');
  await form.getByLabel('KV v2 mount').fill('secret');
  await form.getByLabel('Credential source').fill('env:VAULT_TOKEN');
  await form.getByLabel('Cache TTL (seconds)').fill('45');
  await form.getByRole('button', { name: 'Save provider' }).click();
  await expect(page.getByTestId('secrets-provider')).toContainText('vault · unavailable');
  expect(fake.providers).toEqual([expect.objectContaining({ scope: 'memowikis', provider: 'vault', ttlSeconds: 45 })]);
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(shots, `secrets-provider-${theme}--mocked.png`) });
  }
  await page.getByTestId('secrets-scope-project').selectOption('');
  await expect(page.getByTestId('secrets-provider')).toContainText('workspace: builtin');
});
