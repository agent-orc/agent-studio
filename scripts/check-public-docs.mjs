#!/usr/bin/env node

// Fails when public documentation carries things that stopped being true after
// the move to the agent-orc org: retired repository owners, the maintainer's
// own machine paths, or the test host's real address.
//
// With --verify-registry it additionally asks npm and NuGet whether the release
// claims recorded below still hold. --verify-install-urls checks source and
// release installation URLs in README and the install guide.
//
//   node scripts/check-public-docs.mjs
//   node scripts/check-public-docs.mjs --verify-registry
//   node scripts/check-public-docs.mjs --verify-install-urls

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(import.meta.dirname, '..');

// Each rule is deliberately narrow. A doc-truth check that cries wolf gets
// switched off, so only unambiguous leaks belong here.
const rules = [
  {
    name: 'retired personal repository owner',
    // The org is agent-orc. ai-patterns.dev genuinely still lives on the
    // personal account, so it is the one allowed exception.
    pattern: /github\.com\/RobertMischke\/(?!ai-patterns\.dev)/gi,
    hint: 'link to the agent-orc repository instead',
  },
  {
    name: "the maintainer's Windows profile",
    // Matches both C:\Users\rmisc and the JSON-escaped C:\\Users\\rmisc.
    pattern: /C:\\+Users\\+rmisc/gi,
    hint: 'use %APPDATA% or a <you> placeholder',
  },
  {
    name: "the test runner host's real address",
    pattern: /88\.99\.136\.78/g,
    hint: 'use the <runner-host-ip> placeholder',
  },
];

// Version claims made in public docs. The assertion is "this version is
// published", not "this is the latest version", so a new release does not turn
// the check red.
const registryClaims = [
  { registry: 'NuGet', id: 'TokenEconomy', version: '0.3.5' },
  { registry: 'NuGet', id: 'CodingAgentRunner', version: '0.7.0' },
  { registry: 'npm', id: 'coding-agent-chat', version: '0.3.2' },
];

function docFiles(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) {
      if (entry === 'node_modules' || entry === '.git') continue;
      docFiles(path, out);
    } else if (entry.endsWith('.md') || entry.endsWith('.html')) {
      out.push(path);
    }
  }
  return out;
}

function lineOf(text, index) {
  return text.slice(0, index).split('\n').length;
}

function checkDocs() {
  const findings = [];
  const files = [join(root, 'README.md'), ...docFiles(join(root, 'docs'))];
  for (const file of files) {
    const text = readFileSync(file, 'utf8');
    for (const rule of rules) {
      for (const match of text.matchAll(rule.pattern)) {
        findings.push(
          `${relative(root, file).replace(/\\/g, '/')}:${lineOf(text, match.index)}: ${rule.name} (${rule.hint})`,
        );
      }
    }
  }
  return findings;
}

async function checkRegistry() {
  const findings = [];
  for (const claim of registryClaims) {
    const url =
      claim.registry === 'NuGet'
        ? `https://api.nuget.org/v3-flatcontainer/${claim.id.toLowerCase()}/index.json`
        : `https://registry.npmjs.org/${claim.id}`;
    try {
      const response = await fetch(url, { headers: { 'user-agent': 'agent-orc-docs-check' } });
      if (!response.ok) {
        findings.push(`${claim.registry} ${claim.id}: registry returned HTTP ${response.status}`);
        continue;
      }
      const body = await response.json();
      const versions = claim.registry === 'NuGet' ? body.versions : Object.keys(body.versions ?? {});
      if (!versions.includes(claim.version)) {
        findings.push(
          `${claim.registry} ${claim.id} ${claim.version}: not published (found ${versions.slice(-5).join(', ') || 'nothing'})`,
        );
      }
    } catch (error) {
      findings.push(`${claim.registry} ${claim.id}: lookup failed: ${error.message}`);
    }
  }
  return findings;
}

export function installUrls(source) {
  const urls = new Set();
  for (const match of source.matchAll(/https:\/\/[^\s<>'"`\])]+/g)) {
    const url = match[0].replace(/[.,;]+$/, '');
    const parsed = new URL(url);
    const path = parsed.pathname;
    if (/(?:^|\/)(?:install\.sh|agent-studio-setup(?:\.exe)?|SHA256SUMS|agent-orchestrator-[^/]+\.(?:tar\.gz|zip))$/.test(path)
      || (parsed.hostname === 'github.com'
        && (path === '/agent-orc/agent-studio.git'
          || path.startsWith('/agent-orc/agent-studio/releases/')))) {
      urls.add(url);
    }
  }
  return [...urls];
}

export async function verifyInstallUrls(entries, fetcher = fetch) {
  const findings = [];
  for (const { name, url } of entries) {
    try {
      const response = await fetcher(url, {
        headers: { 'user-agent': 'agent-orc-docs-check' },
        signal: AbortSignal.timeout(15000),
      });
      await response.body?.cancel();
      if (response.status !== 200) findings.push(`${name}: ${url} returned HTTP ${response.status}`);
    } catch (error) {
      findings.push(`${name}: ${url} could not be downloaded: ${error.message}`);
    }
  }
  return findings;
}

async function checkInstallUrls() {
  const sources = ['README.md', 'docs/operations/setup/install.md'].map((name) => ({
    name, urls: installUrls(readFileSync(join(root, name), 'utf8')),
  }));
  const entries = sources.flatMap(({ name, urls }) => urls.map((url) => ({ name, url })));
  const findings = await verifyInstallUrls(entries);
  if (sources[0].urls.some((url) => new URL(url).pathname.endsWith('/install.sh'))) {
    findings.push(...(await checkNightlyProof()));
  }
  return findings;
}

export function nightlyProofFindings(runs) {
  const latest = runs.slice(0, 3);
  const days = latest.map((run) => Date.parse(`${run.created_at?.slice(0, 10)}T00:00:00Z`));
  if (latest.length < 3 || latest.some((run) =>
    run.event !== 'schedule' || run.head_branch !== 'main' || run.conclusion !== 'success')
    || days.some((day) => Number.isNaN(day))
    || days[0] - days[1] !== 86400000 || days[1] - days[2] !== 86400000) {
    return ['README.md: the one-line installer needs three consecutive successful scheduled installation nights on main'];
  }
  return [];
}

async function checkNightlyProof() {
  const url = 'https://api.github.com/repos/agent-orc/agent-studio/actions/workflows/installation-nightly.yml/runs?branch=main&event=schedule&per_page=3';
  try {
    const token = process.env.GITHUB_TOKEN;
    const response = await fetch(url, {
      headers: {
        accept: 'application/vnd.github+json',
        'user-agent': 'agent-orc-docs-check',
        ...(token ? { authorization: `Bearer ${token}` } : {}),
      },
      signal: AbortSignal.timeout(15000),
    });
    if (response.status !== 200) return [`README.md: nightly installation history returned HTTP ${response.status}`];
    const body = await response.json();
    return nightlyProofFindings(body.workflow_runs ?? []);
  } catch (error) {
    return [`README.md: nightly installation history could not be checked: ${error.message}`];
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const findings = checkDocs();
  if (process.argv.includes('--verify-registry')) findings.push(...(await checkRegistry()));
  if (process.argv.includes('--verify-install-urls')) findings.push(...(await checkInstallUrls()));
  if (findings.length > 0) {
    console.error('Public documentation truth check failed:');
    for (const finding of findings) console.error(`- ${finding}`);
    process.exit(1);
  }
  console.log('Public documentation truth check passed.');
}
