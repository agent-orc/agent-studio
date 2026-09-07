#!/usr/bin/env node

// Fails when public documentation carries things that stopped being true after
// the move to the agent-orc org: retired repository owners, the maintainer's
// own machine paths, or the test host's real address.
//
// With --verify-registry it additionally asks npm and NuGet whether the release
// claims recorded below still hold. That needs network, so CI runs it on a
// schedule rather than on every pull request.
//
//   node scripts/check-public-docs.mjs
//   node scripts/check-public-docs.mjs --verify-registry

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

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
  { registry: 'NuGet', id: 'TokenEconomy', version: '0.3.3' },
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

const findings = checkDocs();
if (process.argv.includes('--verify-registry')) {
  findings.push(...(await checkRegistry()));
}

if (findings.length > 0) {
  console.error('Public documentation truth check failed:');
  for (const finding of findings) console.error(`- ${finding}`);
  process.exit(1);
}

console.log('Public documentation truth check passed.');
