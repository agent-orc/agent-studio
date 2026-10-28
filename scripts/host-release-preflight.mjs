#!/usr/bin/env node
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export function compareVersions(left, right) {
  const parse = value => /^\d+\.\d+\.\d+$/.test(value ?? '')
    ? value.split('.').map(Number) : null;
  const a = parse(left);
  const b = parse(right);
  if (!a || !b) return null;
  for (let index = 0; index < 3; index += 1) {
    if (a[index] !== b[index]) return Math.sign(a[index] - b[index]);
  }
  return 0;
}

export function inspectHosts(hosts, requirement, ancestor) {
  return hosts.filter(host => host.status === 'active').map(host => {
    const release = host.release;
    const versionOrder = compareVersions(release?.version, requirement.minimumAgentHostRelease);
    let reason = null;
    if (versionOrder === null) reason = 'release version is missing or cannot be compared';
    else if (versionOrder < 0) reason = `release ${release.version} is older than ${requirement.minimumAgentHostRelease}`;
    else if (!release?.commit) reason = 'release commit is missing';
    else if (!ancestor(requirement.minimumAgentHostCommit, release.commit))
      reason = `release commit ${release.commit} lacks the required host protocol`;
    return reason ? { hostId: host.hostId ?? host.name ?? host.runnerId,
      runnerId: host.runnerId, version: release?.version ?? null, reason } : null;
  }).filter(Boolean);
}

function args(argv) {
  const values = {};
  for (let i = 0; i < argv.length; i += 2) {
    if (!argv[i].startsWith('--') || !argv[i + 1]) throw new Error(`Invalid argument ${argv[i]}`);
    values[argv[i].slice(2)] = argv[i + 1];
  }
  for (const required of ['manifest', 'config', 'url', 'repo', 'target', 'record']) {
    if (!values[required]) throw new Error(`--${required} is required`);
  }
  return values;
}

async function loadHosts(options) {
  if (process.env.ATP_HOST_PREFLIGHT_SNAPSHOT_FILE)
    return JSON.parse(await readFile(process.env.ATP_HOST_PREFLIGHT_SNAPSHOT_FILE, 'utf8'));
  const config = JSON.parse(await readFile(options.config, 'utf8'));
  const server = config.TaskServer ?? {};
  const token = server.AuthTokenFile
    ? (await readFile(server.AuthTokenFile, 'utf8')).trim() : server.AuthToken?.trim();
  const response = await fetch(`${options.url.replace(/\/$/, '')}/api/v1/management/remote-hosts`, {
    headers: {
      'X-Task-Protocol-Version': '1',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok) throw new Error(`Host registry returned HTTP ${response.status}`);
  return response.json();
}

export async function run(argv) {
  const options = args(argv);
  const requirement = JSON.parse(await readFile(options.manifest, 'utf8'));
  const hosts = await loadHosts(options);
  if (!Array.isArray(hosts)) throw new Error('Host registry did not return a host list');
  const incompatible = inspectHosts(hosts, requirement, (minimum, running) => {
    try {
      execFileSync('git', ['-C', options.repo, 'merge-base', '--is-ancestor', minimum, running],
        { stdio: 'ignore', windowsHide: true });
      return true;
    } catch { return false; }
  });
  const record = {
    target: options.target,
    checkedAt: new Date().toISOString(),
    minimumAgentHostRelease: requirement.minimumAgentHostRelease,
    minimumAgentHostCommit: requirement.minimumAgentHostCommit,
    incompatibleHosts: incompatible,
    forced: process.env.ATP_FORCE_HOST_COMPATIBILITY === '1',
  };
  await mkdir(resolve(options.record, '..'), { recursive: true });
  await writeFile(options.record, `${JSON.stringify(record, null, 2)}\n`);
  for (const host of incompatible)
    console.error(`[host-release-preflight] ${host.hostId} (${host.runnerId}): ${host.reason}`);
  console.log(`[host-release-preflight] Minimum agent-host ${requirement.minimumAgentHostRelease} (${requirement.minimumAgentHostCommit.slice(0, 10)}); ${incompatible.length} incompatible host(s). Record: ${options.record}`);
  if (incompatible.length && !record.forced) throw new Error('Registered hosts need an upgrade; set ATP_FORCE_HOST_COMPATIBILITY=1 to force deployment.');
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
  run(process.argv.slice(2)).catch(error => {
    console.error(`[host-release-preflight] ${error.message}`);
    process.exitCode = 1;
  });
