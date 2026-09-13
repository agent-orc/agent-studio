import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { chmodSync, mkdtempSync, mkdirSync, readFileSync, readlinkSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url));
const BUILD = join(SCRIPT_DIR, 'build-demo-release.mjs');
const VERIFY = join(SCRIPT_DIR, 'verify-demo-release.mjs');
const RESET = resolve(SCRIPT_DIR, '../../deploy/demo-runtime/reset-demo-runtime.sh');
const ROLLBACK = resolve(SCRIPT_DIR, '../../deploy/demo-runtime/rollback-demo-runtime.sh');
const PRODUCT_IMAGE = `sha256:${'a'.repeat(64)}`;

function writeInputs(root, compatibility = { minimum: 1, maximum: 1 }) {
  const replay = join(root, 'replay.json');
  const policy = join(root, 'deployment-policy.json');
  const sourceTerms = join(root, 'private-source-terms.txt');
  writeFileSync(replay, `${JSON.stringify({ schemaVersion: 1, events: [] }, null, 2)}\n`);
  writeFileSync(policy, `${JSON.stringify({
    schemaVersion: 1,
    id: 'public-demo-readonly',
    compatibility: { seedSchema: compatibility },
  }, null, 2)}\n`);
  writeFileSync(sourceTerms, 'private-source-name-that-must-not-ship\n');
  return { replay, policy, sourceTerms };
}

function build(release, output, inputs, review) {
  const args = [
    BUILD,
    '--release', release,
    '--output-dir', output,
    '--product-image', PRODUCT_IMAGE,
    '--replay-trace', inputs.replay,
    '--deployment-policy', inputs.policy,
    '--source-terms-file', inputs.sourceTerms,
  ];
  if (review) args.push('--human-review', review);
  return JSON.parse(execFileSync(process.execPath, args, {
    encoding: 'utf8',
    env: { ...process.env, SOURCE_DATE_EPOCH: '1' },
    windowsHide: true,
  }));
}

test('builds a scrubbed two-pass candidate and invalidates approval on any changed byte', () => {
  const root = mkdtempSync(join(tmpdir(), 'demo-release-test-'));
  try {
    const inputs = writeInputs(root);
    const candidateOutput = join(root, 'candidate');
    const candidate = build('2026.08.1', candidateOutput, inputs);
    const report = JSON.parse(readFileSync(candidate.scrubReport, 'utf8'));
    assert.equal(candidate.releaseState, 'candidate');
    assert.equal(report.status, 'machine-passed-human-pending');
    assert.equal(report.scanners.unreviewedMatchCount, 0);
    assert.equal(report.twoPassProof.recursiveDiff, 'clean');
    assert.ok(report.images.count > 0);
    assert.ok(report.images.results.every((image) => image.metadata.status === 'clean'));
    assert.ok(report.images.results.every((image) => image.ocr.status === 'no-text-detected'));
    assert.equal(report.privateSourceTerms.shipped, false);

    const candidateExtract = join(root, 'candidate-extract');
    execFileSync(process.execPath, [VERIFY, '--bundle', candidate.bundle, '--extract-to', candidateExtract], { stdio: 'pipe', windowsHide: true });
    const rejected = spawnSync(process.execPath, [VERIFY, '--directory', candidateExtract, '--require-approved'], { encoding: 'utf8', windowsHide: true });
    assert.notEqual(rejected.status, 0);
    assert.match(rejected.stderr, /human scrub approval is missing/i);

    const review = join(root, 'human-review.json');
    writeFileSync(review, `${JSON.stringify({
      decision: 'approved',
      reviewer: 'Demo release test reviewer',
      reviewedAt: '2026-08-17T09:00:00.000Z',
      reviewedContentDigest: report.generatedDatastore.contentDigest,
      reviewedManifestDigest: report.generatedDatastore.manifestDigest,
      exceptions: [],
    }, null, 2)}\n`);
    const approvedOutput = join(root, 'approved');
    const approved = build('2026.08.2', approvedOutput, inputs, review);
    assert.equal(approved.releaseState, 'approved');
    const approvedExtract = join(root, 'approved-extract');
    execFileSync(process.execPath, [VERIFY, '--bundle', approved.bundle, '--extract-to', approvedExtract, '--require-approved'], { stdio: 'pipe', windowsHide: true });

    const readme = join(approvedExtract, 'runtime', 'datastore', 'README.md');
    writeFileSync(readme, `${readFileSync(readme, 'utf8')}tampered\n`);
    const tampered = spawnSync(process.execPath, [VERIFY, '--directory', approvedExtract, '--require-approved'], { encoding: 'utf8', windowsHide: true });
    assert.notEqual(tampered.status, 0);
    assert.match(tampered.stderr, /immutable manifest/i);
  } finally {
    spawnSync('chmod', ['-R', 'u+w', root], { windowsHide: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test('compatibility gate rejects a seed schema outside the deployment range', () => {
  const root = mkdtempSync(join(tmpdir(), 'demo-release-compatibility-test-'));
  try {
    const inputs = writeInputs(root, { minimum: 2, maximum: 3 });
    mkdirSync(join(root, 'output'));
    const result = spawnSync(process.execPath, [
      BUILD,
      '--release', '2026.08.1',
      '--output-dir', join(root, 'output'),
      '--product-image', PRODUCT_IMAGE,
      '--replay-trace', inputs.replay,
      '--deployment-policy', inputs.policy,
      '--source-terms-file', inputs.sourceTerms,
    ], { encoding: 'utf8', windowsHide: true });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /outside deployment compatibility/i);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('private source-name matches fail closed without persisting the matched value', () => {
  const root = mkdtempSync(join(tmpdir(), 'demo-release-source-term-test-'));
  try {
    const inputs = writeInputs(root);
    const privateTerm = 'Agent Studio demonstrations';
    writeFileSync(inputs.sourceTerms, `${privateTerm}\n`);
    const output = join(root, 'output');
    const result = spawnSync(process.execPath, [
      BUILD,
      '--release', '2026.08.1',
      '--output-dir', output,
      '--product-image', PRODUCT_IMAGE,
      '--replay-trace', inputs.replay,
      '--deployment-policy', inputs.policy,
      '--source-terms-file', inputs.sourceTerms,
    ], { encoding: 'utf8', windowsHide: true });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /scrub gate failed/i);
    const reportText = readFileSync(join(output, 'demo-seed-scrub-report.json'), 'utf8');
    const report = JSON.parse(reportText);
    assert.ok(report.violations.some((violation) => violation.category === 'source-term'));
    assert.doesNotMatch(reportText, new RegExp(privateTerm, 'i'));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('reset replaces the whole release, retains the healthy predecessor, and rehearses rollback', () => {
  const root = mkdtempSync(join(tmpdir(), 'demo-runtime-reset-test-'));
  try {
    const inputs = writeInputs(root);
    const candidate = build('2026.08.10', join(root, 'candidate'), inputs);
    const report = JSON.parse(readFileSync(candidate.scrubReport, 'utf8'));
    const review = join(root, 'human-review.json');
    writeFileSync(review, `${JSON.stringify({
      decision: 'approved',
      reviewer: 'Reset rehearsal test reviewer',
      reviewedAt: '2026-08-17T10:00:00.000Z',
      reviewedContentDigest: report.generatedDatastore.contentDigest,
      reviewedManifestDigest: report.generatedDatastore.manifestDigest,
      exceptions: [],
    }, null, 2)}\n`);
    const first = build('2026.08.11', join(root, 'release-one'), inputs, review);
    const second = build('2026.08.12', join(root, 'release-two'), inputs, review);

    const hooks = join(root, 'hooks');
    mkdirSync(hooks);
    for (const name of ['start', 'probe', 'switch', 'stop']) {
      const path = join(hooks, name);
      writeFileSync(path, [
        '#!/usr/bin/env bash',
        'set -eu',
        `printf '${name} %s\\n' "$(basename "$1")" >>"\${HOOK_LOG:?}"`,
        name === 'probe' ? 'if [[ -f "${FAIL_PROBE_FILE:-/nonexistent}" ]]; then exit 1; fi' : ':',
        '',
      ].join('\n'));
      chmodSync(path, 0o755);
    }
    const runtime = join(root, 'runtime-root');
    const hookLog = join(root, 'hooks.log');
    const environment = { ...process.env, HOOK_LOG: hookLog };
    const resetArgs = (release) => [RESET, '--bundle', release.bundle, '--bundle-digest', `sha256:${release.bundleDigest}`, '--runtime-root', runtime, '--start-hook', join(hooks, 'start'), '--probe-hook', join(hooks, 'probe'), '--switch-hook', join(hooks, 'switch'), '--stop-hook', join(hooks, 'stop')];
    execFileSync('bash', resetArgs(first), { env: environment, stdio: 'pipe', windowsHide: true });
    const firstRoot = readlinkSync(join(runtime, 'current'));
    assert.equal(JSON.parse(readFileSync(join(firstRoot, 'release', 'demo-release-manifest.json'))).demoRelease, '2026.08.11');
    const driftedReadme = join(firstRoot, 'runtime', 'datastore', 'README.md');
    writeFileSync(driftedReadme, `${readFileSync(driftedReadme, 'utf8')}runtime drift\n`);
    const wrongDigestArgs = resetArgs(second);
    wrongDigestArgs[wrongDigestArgs.indexOf('--bundle-digest') + 1] = `sha256:${'b'.repeat(64)}`;
    const wrongDigest = spawnSync('bash', wrongDigestArgs, { env: environment, encoding: 'utf8', windowsHide: true });
    assert.notEqual(wrongDigest.status, 0);
    assert.equal(readlinkSync(join(runtime, 'current')), firstRoot);
    execFileSync('bash', resetArgs(second), { env: environment, stdio: 'pipe', windowsHide: true });
    const secondRoot = readlinkSync(join(runtime, 'current'));
    assert.equal(JSON.parse(readFileSync(join(secondRoot, 'release', 'demo-release-manifest.json'))).demoRelease, '2026.08.12');
    assert.equal(readlinkSync(join(runtime, 'previous')), firstRoot);

    execFileSync('bash', [ROLLBACK, '--runtime-root', runtime, '--start-hook', join(hooks, 'start'), '--probe-hook', join(hooks, 'probe'), '--switch-hook', join(hooks, 'switch'), '--stop-hook', join(hooks, 'stop')], { env: environment, stdio: 'pipe', windowsHide: true });
    const rollbackRoot = readlinkSync(join(runtime, 'current'));
    assert.notEqual(rollbackRoot, firstRoot);
    assert.equal(JSON.parse(readFileSync(join(rollbackRoot, 'release', 'demo-release-manifest.json'))).demoRelease, '2026.08.11');
    assert.doesNotMatch(readFileSync(join(rollbackRoot, 'runtime', 'datastore', 'README.md'), 'utf8'), /runtime drift/);
    assert.equal(readlinkSync(join(runtime, 'previous')), secondRoot);
    assert.equal(readdirSync(join(runtime, 'releases')).filter((name) => !name.startsWith('.')).length, 2);

    const failProbe = join(root, 'fail-probe');
    writeFileSync(failProbe, 'fail\n');
    const failed = spawnSync('bash', resetArgs(second), { env: { ...environment, FAIL_PROBE_FILE: failProbe }, encoding: 'utf8', windowsHide: true });
    assert.notEqual(failed.status, 0);
    assert.equal(readlinkSync(join(runtime, 'current')), rollbackRoot);
    assert.equal(readdirSync(join(runtime, 'releases')).filter((name) => name.startsWith('.candidate.') || name.startsWith('.rollback.')).length, 0);
    assert.match(readFileSync(hookLog, 'utf8'), /switch/);
  } finally {
    spawnSync('chmod', ['-R', 'u+w', root], { windowsHide: true });
    rmSync(root, { recursive: true, force: true });
  }
});
