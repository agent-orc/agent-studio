#!/usr/bin/env node

import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));

function readOptions(argv) {
  const options = {
    frontendDir: resolve(scriptDir, '..', 'frontend'),
    url: 'http://127.0.0.1:4011',
    timeoutMs: 180_000,
    settleMs: 2_000,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    const value = argv[index + 1];
    if (!value) throw new Error(`Missing value for ${flag}.`);

    switch (flag) {
      case '--frontend-dir':
        options.frontendDir = resolve(value);
        break;
      case '--url':
        options.url = value;
        break;
      case '--timeout-ms':
        options.timeoutMs = positiveInteger(value, flag);
        break;
      case '--settle-ms':
        options.settleMs = nonNegativeInteger(value, flag);
        break;
      default:
        throw new Error(`Unknown argument: ${flag}.`);
    }
    index += 1;
  }

  return options;
}

function positiveInteger(value, flag) {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    throw new Error(`${flag} must be a positive integer.`);
  }
  return parsed;
}

function nonNegativeInteger(value, flag) {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < 0) {
    throw new Error(`${flag} must be a non-negative integer.`);
  }
  return parsed;
}

function delay(milliseconds) {
  return new Promise(resolveDelay => setTimeout(resolveDelay, milliseconds));
}

function errorText(error) {
  if (error instanceof Error) return error.stack ?? `${error.name}: ${error.message}`;
  return String(error);
}

function isStartupConnectionError(error) {
  const message = errorText(error);
  return error?.name === 'TimeoutError'
    || /ERR_CONNECTION_REFUSED|ERR_CONNECTION_RESET|ERR_CONNECTION_CLOSED|ECONNREFUSED|socket hang up|Timeout .* exceeded/i.test(message);
}

// AGT-2862: `ng serve` binds one address family, and which one depends on how
// the host resolves the name it was given, so a probe pinned to a single
// loopback spelling reports a running frontend as missing. Every spelling of
// the configured loopback origin is tried before the boot is called failed;
// a non-loopback origin is left alone, because widening it would probe a
// different machine.
function loopbackProbeTargets(url) {
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    return [url];
  }
  const host = parsed.hostname.replace(/^\[|\]$/g, '');
  const isLoopback = host === 'localhost' || host === '::1' || /^127(\.\d{1,3}){3}$/.test(host);
  if (!isLoopback) return [url];

  const targets = [];
  for (const candidate of [host, 'localhost', '127.0.0.1', '::1']) {
    const next = new URL(url);
    next.hostname = candidate.includes(':') ? `[${candidate}]` : candidate;
    const origin = next.toString();
    if (!targets.includes(origin)) targets.push(origin);
  }
  return targets;
}

async function loadOnceFrontendAcceptsConnections(page, url, deadline) {
  const targets = loopbackProbeTargets(url);
  const lastError = new Map();

  while (true) {
    for (const target of targets) {
      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        const tried = targets
          .map(t => `  ${t}: ${lastError.get(t) ?? 'not reached'}`)
          .join('\n');
        throw new Error(
          `Timed out waiting for ${url} to accept a browser navigation. Origins tried:\n${tried}`,
        );
      }

      try {
        return await page.goto(target, {
          waitUntil: 'domcontentloaded',
          timeout: Math.min(remaining, 15_000),
        });
      } catch (error) {
        if (!isStartupConnectionError(error)) throw error;
        lastError.set(target, errorText(error).split('\n')[0]);
      }
    }

    if (Date.now() >= deadline) continue;
    await delay(Math.min(250, Math.max(1, deadline - Date.now())));
  }
}

async function run() {
  const options = readOptions(process.argv.slice(2));
  const requireFromFrontend = createRequire(join(options.frontendDir, 'package.json'));
  const { chromium } = requireFromFrontend('playwright-core');
  const executablePath = process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
  const browser = await chromium.launch({
    headless: true,
    ...(executablePath ? { executablePath } : {}),
  });

  try {
    const page = await browser.newPage();
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(errorText(error)));

    const response = await loadOnceFrontendAcceptsConnections(
      page,
      options.url,
      Date.now() + options.timeoutMs,
    );
    if (!response) throw new Error(`Navigation to ${options.url} returned no response.`);
    if (typeof response.ok === 'function' && !response.ok()) {
      const status = typeof response.status === 'function' ? response.status() : 'unknown';
      throw new Error(`Navigation to ${options.url} returned HTTP ${status}.`);
    }

    await delay(options.settleMs);
    if (pageErrors.length > 0) {
      throw new Error(`PAGEERROR while booting ${options.url}:\n${pageErrors.join('\n---\n')}`);
    }

    console.log(`[stable-frontend-probe] Boot completed without page errors: ${options.url}`);
  } finally {
    await browser.close();
  }
}

run().catch(error => {
  console.error(`[stable-frontend-probe] ${errorText(error)}`);
  process.exitCode = 1;
});
