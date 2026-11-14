import { expect, test } from '@playwright/test';
import { constants, existsSync, readFileSync, realpathSync, accessSync } from 'node:fs';
import { join, delimiter } from 'node:path';

test('isolated backend keeps provider processes and local state in its workspace', async () => {
  test.skip(process.env.PW_PREBUILT !== '1' || process.platform !== 'linux',
    'This guard inspects the Linux prebuilt backend process.');

  const workspace = realpathSync(process.env.PW_E2E_WORKSPACE!);
  const port = process.env.PW_E2E_BACKEND_PORT!;
  const pid = readFileSync(join(__dirname, '..', '..', '..', `.api.${port}.pid`), 'utf8').trim();
  const environment = Object.fromEntries(readFileSync(`/proc/${pid}/environ`, 'utf8')
    .split('\0').filter(Boolean).map(entry => {
      const separator = entry.indexOf('=');
      return [entry.slice(0, separator), entry.slice(separator + 1)];
    }));

  for (const [key, relative] of Object.entries({
    HOME: 'home',
    XDG_CONFIG_HOME: 'config',
    XDG_DATA_HOME: 'data',
    XDG_CACHE_HOME: 'cache',
    CODEX_HOME: 'home/.codex',
    CLAUDE_CONFIG_DIR: 'home/.claude',
  })) {
    expect(environment[key], key).toBe(join(workspace, relative));
  }

  const pathEntries = environment.PATH.split(delimiter);
  expect(pathEntries[0]).toBe(join(workspace, 'bin'));
  for (const [provider, configKey] of Object.entries({
    codex: 'CodexCli__Path',
    claude: 'ClaudeCli__Path',
    copilot: 'CopilotCli__Path',
    gemini: 'GeminiCli__Path',
    antigravity: 'AntigravityCli__Path',
  })) {
    const resolved = pathEntries.map(directory => join(directory, provider)).find(candidate => {
      try { accessSync(candidate, constants.X_OK); return true; } catch { return false; }
    });
    expect(resolved, `${provider} must resolve from PATH`).toBe(join(workspace, 'bin', provider));
    expect(realpathSync(environment[configKey]), configKey).toBe(resolved);
  }

  const logPath = join(__dirname, '..', '..', '..', `.api.${port}.log.out`);
  await expect.poll(() => existsSync(logPath) ? readFileSync(logPath, 'utf8') : '',
    { timeout: 30_000 }).toContain(`local-state-root ${join(workspace, 'data')}`);
  await expect.poll(() => {
    const invocations = join(workspace, 'provider-cli-invocations.log');
    return existsSync(invocations) ? readFileSync(invocations, 'utf8') : '';
  }, { timeout: 30_000 }).toMatch(/^codex$/m);
});
