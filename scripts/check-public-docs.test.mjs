import assert from 'node:assert/strict';
import test from 'node:test';
import { installUrls, nightlyProofFindings, verifyInstallUrls } from './check-public-docs.mjs';

test('finds source and release install URLs in both Markdown forms', () => {
  const source = [
    '`https://github.com/agent-orc/agent-studio/releases/latest/download/install.sh`',
    '[setup](https://github.com/agent-orc/agent-studio/releases/download/v1.2.3/agent-studio-setup)',
    'https://github.com/agent-orc/agent-studio/releases/latest/download/SHA256SUMS',
    '[releases](https://github.com/agent-orc/agent-studio/releases/latest)',
    'git clone https://github.com/agent-orc/agent-studio.git',
    'https://github.com/agent-orc/agent-studio/releases/latest/download/install.sh',
    'https://github.com/agent-orc/agent-studio',
  ].join('\n');
  assert.deepEqual(installUrls(source), [
    'https://github.com/agent-orc/agent-studio/releases/latest/download/install.sh',
    'https://github.com/agent-orc/agent-studio/releases/download/v1.2.3/agent-studio-setup',
    'https://github.com/agent-orc/agent-studio/releases/latest/download/SHA256SUMS',
    'https://github.com/agent-orc/agent-studio/releases/latest',
    'https://github.com/agent-orc/agent-studio.git',
  ]);
});

test('requires HTTP 200 for each install asset', async () => {
  const entries = [
    { name: 'README.md', url: 'https://example.test/install.sh' },
    { name: 'docs/operations/setup/install.md', url: 'https://example.test/SHA256SUMS' },
  ];
  const findings = await verifyInstallUrls(entries, async (url) => ({
    status: url.endsWith('install.sh') ? 200 : 404,
  }));
  assert.deepEqual(findings, [
    'docs/operations/setup/install.md: https://example.test/SHA256SUMS returned HTTP 404',
  ]);
});

test('one-line installer needs three consecutive successful scheduled nights', () => {
  const success = { event: 'schedule', head_branch: 'main', conclusion: 'success' };
  const nights = [
    { ...success, created_at: '2026-11-09T02:18:00Z' },
    { ...success, created_at: '2026-11-08T02:18:00Z' },
    { ...success, created_at: '2026-11-07T02:18:00Z' },
  ];
  assert.deepEqual(nightlyProofFindings(nights), []);
  assert.equal(nightlyProofFindings([nights[0], { ...nights[1], conclusion: 'failure' }, nights[2]]).length, 1);
  assert.equal(nightlyProofFindings([nights[0], nights[2], nights[2]]).length, 1);
  assert.equal(nightlyProofFindings(nights.slice(0, 2)).length, 1);
});
