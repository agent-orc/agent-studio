#!/usr/bin/env node

import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

import {
  RELEASE_SCHEMA_VERSION,
  assertSafeArchiveEntry,
  buildTreeManifest,
  sha256File,
} from './demo-release-lib.mjs';

function usage(message) {
  if (message) console.error(message);
  console.error('Usage: verify-demo-release.mjs (--bundle <archive> | --directory <release-root>) [--extract-to <empty-dir>] [--expected-bundle-digest sha256:<digest>] [--require-approved]');
  process.exit(2);
}

function parseArgs(argv) {
  const args = { requireApproved: false };
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (name === '--require-approved') args.requireApproved = true;
    else {
      const value = argv[++index];
      if (!value) usage(`Missing value for ${name}`);
      if (name === '--bundle') args.bundle = resolve(value);
      else if (name === '--directory') args.directory = resolve(value);
      else if (name === '--extract-to') args.extractTo = resolve(value);
      else if (name === '--expected-bundle-digest') args.expectedBundleDigest = value;
      else usage(`Unknown argument: ${name}`);
    }
  }
  if (Boolean(args.bundle) === Boolean(args.directory)) usage('Choose exactly one of --bundle or --directory.');
  if (args.extractTo && !args.bundle) usage('--extract-to is valid only with --bundle.');
  if (args.expectedBundleDigest && !args.bundle) usage('--expected-bundle-digest is valid only with --bundle.');
  if (args.expectedBundleDigest && !/^sha256:[a-f0-9]{64}$/u.test(args.expectedBundleDigest)) usage('--expected-bundle-digest must be sha256:<64 lowercase hex>.');
  return args;
}

function run(command, commandArgs) {
  const result = spawnSync(command, commandArgs, {
    encoding: 'utf8',
    maxBuffer: 128 * 1024 * 1024,
    windowsHide: true,
  });
  if (result.status !== 0) throw new Error(`${command} failed with exit ${result.status}: ${(result.stderr || result.stdout).trim()}`);
  return result.stdout;
}

function extractArchive(bundle, target) {
  if (!existsSync(bundle)) throw new Error(`Bundle does not exist: ${bundle}`);
  const entries = run('tar', ['-tzf', bundle]).split(/\r?\n/u).filter(Boolean);
  if (entries.length === 0) throw new Error('Bundle archive is empty.');
  for (const entry of entries) assertSafeArchiveEntry(entry.replace(/\/$/u, ''));
  const roots = new Set(entries.map((entry) => entry.split('/')[0]).filter(Boolean));
  if (roots.size !== 1) throw new Error('Bundle archive must have exactly one versioned root directory.');
  if (existsSync(target) && readdirSync(target).length > 0) throw new Error(`Extraction target must be empty: ${target}`);
  mkdirSync(target, { recursive: true });
  run('tar', ['-xzf', bundle, '--strip-components=1', '-C', target]);
  return target;
}

function readJson(path, label) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (error) {
    throw new Error(`${label} is invalid: ${error.message}`);
  }
}

function verify(root, requireApproved) {
  const manifestPath = join(root, 'demo-release-manifest.json');
  const manifest = readJson(manifestPath, 'Release manifest');
  if (manifest.schemaVersion !== RELEASE_SCHEMA_VERSION) throw new Error(`Unsupported release manifest schema: ${manifest.schemaVersion}`);
  if (!/^\d{4}\.\d{2}\.\d+$/u.test(manifest.demoRelease ?? '')) throw new Error('Release manifest has an invalid demoRelease.');
  if (!/^sha256:[a-f0-9]{64}$/u.test(manifest.productImage?.digest ?? '')) throw new Error('Release manifest does not pin an immutable product image digest.');
  if (requireApproved && (manifest.releaseState !== 'approved' || manifest.scrubReport?.approved !== true)) {
    throw new Error('Candidate bundle is not deployable: human scrub approval is missing.');
  }

  const actual = buildTreeManifest(root).files.filter((file) => file.path !== 'demo-release-manifest.json');
  const declared = manifest.files ?? [];
  const actualMap = new Map(actual.map((file) => [file.path, file]));
  const declaredMap = new Map(declared.map((file) => [file.path, file]));
  for (const path of [...new Set([...actualMap.keys(), ...declaredMap.keys()])].sort()) {
    const found = actualMap.get(path);
    const expected = declaredMap.get(path);
    if (!found) throw new Error(`Manifest declares a missing file: ${path}`);
    if (!expected) throw new Error(`Bundle contains an undeclared file: ${path}`);
    if (found.sha256 !== expected.sha256 || found.size !== expected.size || found.kind !== expected.kind) {
      throw new Error(`Bundle file does not match its immutable manifest: ${path}`);
    }
  }

  const scrubPath = join(root, manifest.scrubReport.path);
  if (sha256File(scrubPath) !== manifest.scrubReport.digest) throw new Error('Scrub report digest does not match the release manifest.');
  const scrub = readJson(scrubPath, 'Scrub report');
  if (requireApproved && (scrub.status !== 'approved' || scrub.humanReview?.approved !== true)) throw new Error('Scrub report is not human-approved.');
  if (scrub.scanners?.unreviewedMatchCount !== 0 || scrub.twoPassProof?.recursiveDiff !== 'clean') {
    throw new Error('Scrub report does not prove a clean machine scan and two-pass diff.');
  }

  const runtimeRoot = join(root, manifest.runtime.datastorePath);
  const runtime = buildTreeManifest(runtimeRoot);
  if (runtime.digest !== manifest.runtime.contentDigest || runtime.fileCount !== manifest.runtime.fileCount) {
    throw new Error('Runtime datastore does not match the release content digest.');
  }
  if (runtime.digest !== scrub.generatedDatastore?.contentDigest) throw new Error('Scrub report was not issued for this runtime datastore.');

  const seed = readJson(join(root, manifest.seed.path), 'Pinned seed');
  const supported = manifest.compatibility?.seedSchema;
  if (!supported || seed.schemaVersion < supported.minimum || seed.schemaVersion > supported.maximum) {
    throw new Error('Pinned seed is outside the manifest compatibility range.');
  }
  for (const input of ['seed', 'generator', 'replayTrace', 'deploymentPolicy']) {
    if (sha256File(join(root, manifest[input].path)) !== manifest[input].digest) throw new Error(`${input} digest does not match the release manifest.`);
  }
  return {
    status: 'passed',
    release: manifest.demoRelease,
    releaseState: manifest.releaseState,
    root,
    runtimeDatastore: runtimeRoot,
    contentDigest: runtime.digest,
  };
}

try {
  const args = parseArgs(process.argv.slice(2));
  if (args.bundle && args.expectedBundleDigest && `sha256:${sha256File(args.bundle)}` !== args.expectedBundleDigest) {
    throw new Error('Bundle archive does not match the operator-pinned digest.');
  }
  const root = args.bundle ? extractArchive(args.bundle, args.extractTo ?? usage('--extract-to is required when verifying an archive.')) : args.directory;
  console.log(JSON.stringify(verify(root, args.requireApproved), null, 2));
} catch (error) {
  console.error(`demo release verification failed: ${error.message}`);
  process.exit(1);
}
