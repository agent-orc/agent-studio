#!/usr/bin/env node

import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const protocolVersion = '1';

function options(argv) {
  const result = {
    config: '',
    taskServerUrl: 'http://127.0.0.1:5071',
    backendUrl: 'http://127.0.0.1:5031',
    timeoutMs: 120_000,
    expectedSha: '',
    configOnly: false,
    directOnly: false,
  };
  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (flag === '--config-only' || flag === '--direct-only') {
      result[flag === '--config-only' ? 'configOnly' : 'directOnly'] = true;
      continue;
    }
    const value = argv[index + 1];
    if (!value) throw new Error(`Missing value for ${flag}.`);
    if (flag === '--config') result.config = resolve(value);
    else if (flag === '--task-server-url') result.taskServerUrl = normalizedUrl(value);
    else if (flag === '--backend-url') result.backendUrl = normalizedUrl(value);
    else if (flag === '--timeout-ms') result.timeoutMs = Number(value);
    else if (flag === '--expected-sha') result.expectedSha = value.toLowerCase();
    else throw new Error(`Unknown argument: ${flag}.`);
    index += 1;
  }
  if (!result.config) throw new Error('--config is required.');
  if (!Number.isInteger(result.timeoutMs) || result.timeoutMs <= 0) {
    throw new Error('--timeout-ms must be a positive integer.');
  }
  if (result.expectedSha && !/^[0-9a-f]{40}$/.test(result.expectedSha)) {
    throw new Error('--expected-sha must be a 40-character hexadecimal Git SHA.');
  }
  return result;
}

function normalizedUrl(value) {
  const parsed = new URL(value);
  if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error(`Unsupported URL: ${value}`);
  return parsed.toString().replace(/\/$/, '');
}

async function loadConfiguration(path) {
  const configuration = JSON.parse(await readFile(path, 'utf8'));
  const taskServer = configuration.TaskServer;
  if (!taskServer || typeof taskServer.BaseUrl !== 'string' || !taskServer.BaseUrl.trim()) {
    throw new Error(`${path} must configure TaskServer:BaseUrl for proxy mode.`);
  }
  const directToken = typeof taskServer.AuthToken === 'string' ? taskServer.AuthToken.trim() : '';
  const tokenFile = typeof taskServer.AuthTokenFile === 'string' ? taskServer.AuthTokenFile.trim() : '';
  if (directToken && tokenFile) throw new Error('Configure only one TaskServer proxy credential source.');
  const token = tokenFile ? (await readFile(tokenFile, 'utf8')).trim() : directToken;
  return { baseUrl: normalizedUrl(taskServer.BaseUrl), token };
}

async function probe(url, headers, deadline, label) {
  let lastError;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, { headers, signal: AbortSignal.timeout(10_000) });
      if (response.ok) return response;
      lastError = new Error(`${label} returned HTTP ${response.status}: ${(await response.text()).slice(0, 500)}`);
    } catch (error) {
      lastError = error;
    }
    await new Promise(resolveDelay => setTimeout(resolveDelay, 250));
  }
  throw new Error(`${label} did not become healthy: ${lastError?.message ?? 'deadline exceeded'}`);
}

async function verifyVersion(response, expectedSha, label) {
  if (!expectedSha) return;
  const status = await response.json();
  const serverVersion = typeof status.serverVersion === 'string' ? status.serverVersion : '';
  if (!serverVersion.toLowerCase().includes(expectedSha)) {
    throw new Error(`${label} reports '${serverVersion || '<missing>'}', expected Git SHA '${expectedSha}'.`);
  }
}

async function run() {
  const value = options(process.argv.slice(2));
  const configuration = await loadConfiguration(value.config);
  if (configuration.baseUrl !== value.taskServerUrl) {
    throw new Error(`TaskServer:BaseUrl is '${configuration.baseUrl}', expected '${value.taskServerUrl}'.`);
  }
  console.log(`[task-server-cutover-probe] Proxy configuration selects ${configuration.baseUrl}.`);
  if (value.configOnly) return;

  const headers = {
    'X-Task-Protocol-Version': protocolVersion,
    ...(configuration.token ? { Authorization: `Bearer ${configuration.token}` } : {}),
  };
  const deadline = Date.now() + value.timeoutMs;
  await probe(`${value.taskServerUrl}/readyz`, headers, deadline, 'Task Server readiness');
  const directStatus = await probe(`${value.taskServerUrl}/api/v1/management/status`, headers, deadline, 'Task Server management plane');
  await verifyVersion(directStatus, value.expectedSha, 'Task Server management plane');
  if (!value.directOnly) {
    const proxyStatus = await probe(`${value.backendUrl}/api/v1/management/status`, headers, deadline, 'OrchestratorApi Task Server proxy');
    await verifyVersion(proxyStatus, value.expectedSha, 'OrchestratorApi Task Server proxy');
  }
  console.log(`[task-server-cutover-probe] ${value.directOnly ? 'Task Server' : 'Task Server and proxy'} are healthy.`);
}

run().catch(error => {
  console.error(`[task-server-cutover-probe] ${error.stack ?? error}`);
  process.exitCode = 1;
});
