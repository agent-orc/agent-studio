#!/usr/bin/env node

import { spawnSync } from 'node:child_process';
import {
  copyFileSync,
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  GENERATOR_VERSION,
  RELEASE_SCHEMA_VERSION,
  SCRUB_REPORT_SCHEMA_VERSION,
  buildTreeManifest,
  compareTreeManifests,
  listFiles,
  loadSourceTerms,
  scanGeneratedDatastore,
  sha256,
  sha256File,
  stableJson,
  validateReleaseVersion,
} from './demo-release-lib.mjs';

const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(SCRIPT_DIR, '../..');
const DEFAULT_SEED = join(REPO_ROOT, 'scripts/presentation-capture/pinned-seed.json');
const DEFAULT_GENERATOR = join(REPO_ROOT, 'scripts/seed-demo-workspace.mjs');
const DEFAULT_POLICY = join(SCRIPT_DIR, 'scrub-policy.json');

function usage(message) {
  if (message) console.error(message);
  console.error('Usage: build-demo-release.mjs --release YYYY.MM.N --output-dir <dir> --product-image sha256:<digest> --replay-trace <file> --deployment-policy <file> --source-terms-file <private-file> [--human-review <file>] [--seed <file>] [--generator <file>]');
  process.exit(2);
}

function parseArgs(argv) {
  const args = { seed: DEFAULT_SEED, generator: DEFAULT_GENERATOR, policy: DEFAULT_POLICY };
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (!name.startsWith('--')) usage(`Unexpected argument: ${name}`);
    const value = argv[++index];
    if (!value) usage(`Missing value for ${name}`);
    if (name === '--release') args.release = value;
    else if (name === '--output-dir') args.outputDir = resolve(value);
    else if (name === '--product-image') args.productImage = value;
    else if (name === '--replay-trace') args.replayTrace = resolve(value);
    else if (name === '--deployment-policy') args.deploymentPolicy = resolve(value);
    else if (name === '--source-terms-file') args.sourceTermsFile = resolve(value);
    else if (name === '--human-review') args.humanReview = resolve(value);
    else if (name === '--seed') args.seed = resolve(value);
    else if (name === '--generator') args.generator = resolve(value);
    else if (name === '--scrub-policy') args.policy = resolve(value);
    else usage(`Unknown argument: ${name}`);
  }
  for (const required of ['release', 'outputDir', 'productImage', 'replayTrace', 'deploymentPolicy', 'sourceTermsFile']) {
    if (!args[required]) usage(`Missing required --${required.replace(/[A-Z]/g, (value) => `-${value.toLowerCase()}`)}`);
  }
  validateReleaseVersion(args.release);
  if (!/^sha256:[a-f0-9]{64}$/u.test(args.productImage)) usage('--product-image must be an immutable sha256:<64 lowercase hex> digest.');
  for (const path of [args.seed, args.generator, args.policy, args.replayTrace, args.deploymentPolicy, args.sourceTermsFile]) {
    if (!existsSync(path)) usage(`Release input does not exist: ${path}`);
  }
  return args;
}

function run(command, args, options = {}) {
  const result = spawnSync(command, args, { encoding: 'utf8', ...options, windowsHide: true });
  if (result.status !== 0) {
    const detail = [result.stdout, result.stderr].filter(Boolean).join('\n').trim();
    throw new Error(`${command} failed with exit ${result.status}.${detail ? `\n${detail}` : ''}`);
  }
  return result;
}

function generate(generator, seed, root) {
  const environment = { ...process.env, ATP_DEMO_PINNED_SEED: seed };
  run(process.execPath, [generator, '--root', root], { env: environment });
}

function readJson(path, label) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (error) {
    throw new Error(`${label} is not valid JSON: ${error.message}`);
  }
}

function readVersion(value, label) {
  const version = value?.schemaVersion ?? value?.version;
  if (version === undefined || version === null || version === '') throw new Error(`${label} needs schemaVersion or version.`);
  return version;
}

function validateHumanReview(path, contentDigest, manifestDigest) {
  if (!path) return { status: 'pending', approved: false, requirement: 'A human approval bound to the generated content digest is required before deployment.' };
  const review = readJson(path, 'Human scrub review');
  if (review.decision !== 'approved') throw new Error('Human scrub review decision must be approved.');
  if (!String(review.reviewer ?? '').trim()) throw new Error('Human scrub review needs a reviewer identity.');
  if (!Number.isFinite(Date.parse(review.reviewedAt))) throw new Error('Human scrub review needs a valid reviewedAt timestamp.');
  if (review.reviewedContentDigest !== contentDigest || review.reviewedManifestDigest !== manifestDigest) {
    throw new Error('Human scrub review does not match this generated datastore. A changed byte invalidates approval.');
  }
  if (!Array.isArray(review.exceptions)) throw new Error('Human scrub review exceptions must be an array.');
  return {
    status: 'approved',
    approved: true,
    reviewer: review.reviewer,
    reviewedAt: review.reviewedAt,
    reviewedContentDigest: review.reviewedContentDigest,
    reviewedManifestDigest: review.reviewedManifestDigest,
    exceptions: review.exceptions,
  };
}

function copyInputs(stage, args) {
  const inputDir = join(stage, 'inputs');
  mkdirSync(inputDir, { recursive: true });
  copyFileSync(args.seed, join(inputDir, 'pinned-seed.json'));
  copyFileSync(args.generator, join(inputDir, 'seed-demo-workspace.mjs'));
  copyFileSync(args.replayTrace, join(inputDir, 'replay-trace.json'));
  copyFileSync(args.deploymentPolicy, join(inputDir, 'deployment-policy.json'));
  copyFileSync(args.policy, join(inputDir, 'scrub-policy.json'));
}

function archive(stageParent, stageName, outputPath) {
  const tarPath = `${outputPath}.partial.tar`;
  const gzipPath = `${outputPath}.partial`;
  const epoch = process.env.SOURCE_DATE_EPOCH ?? '0';
  run('tar', ['--sort=name', `--mtime=@${epoch}`, '--owner=0', '--group=0', '--numeric-owner', '-C', stageParent, '-cf', tarPath, stageName]);
  const gzip = spawnSync('gzip', ['-n', '-c', tarPath], {
    encoding: null,
    maxBuffer: 1024 * 1024 * 1024,
    windowsHide: true,
  });
  if (gzip.status !== 0) throw new Error(`gzip failed with exit ${gzip.status}: ${gzip.stderr?.toString('utf8') ?? ''}`);
  writeFileSync(gzipPath, gzip.stdout);
  rmSync(tarPath, { force: true });
  renameSync(gzipPath, outputPath);
}

function build(args) {
  if (existsSync(args.outputDir) && listFiles(args.outputDir).length > 0) {
    throw new Error(`Output directory must be empty so a release cannot be overwritten: ${args.outputDir}`);
  }
  mkdirSync(args.outputDir, { recursive: true });
  const work = mkdtempSync(join(tmpdir(), 'agent-studio-demo-release-'));
  try {
    const firstRoot = join(work, 'generation-a');
    const secondRoot = join(work, 'generation-b');
    mkdirSync(firstRoot);
    mkdirSync(secondRoot);
    generate(args.generator, args.seed, firstRoot);
    generate(args.generator, args.seed, secondRoot);

    const firstManifest = buildTreeManifest(firstRoot);
    const secondManifest = buildTreeManifest(secondRoot);
    const differences = compareTreeManifests(firstManifest, secondManifest);
    if (differences.length > 0 || firstManifest.digest !== secondManifest.digest) {
      throw new Error(`Two-pass datastore generation was not identical (${differences.length} differences).`);
    }

    const policy = readJson(args.policy, 'Scrub policy');
    const sourceTerms = loadSourceTerms(args.sourceTermsFile);
    const scan = scanGeneratedDatastore({ root: firstRoot, manifest: firstManifest, policy, sourceTerms });
    const secondScan = scanGeneratedDatastore({ root: secondRoot, manifest: secondManifest, policy, sourceTerms });
    if (scan.status !== 'passed' || secondScan.status !== 'passed') {
      const evidencePath = join(args.outputDir, 'demo-seed-scrub-report.json');
      const failedReport = {
        schemaVersion: SCRUB_REPORT_SCHEMA_VERSION,
        status: 'failed',
        generatedRoots: [firstRoot, secondRoot],
        violations: [...scan.violations, ...secondScan.violations],
      };
      writeFileSync(evidencePath, stableJson(failedReport));
      throw new Error(`Scrub gate failed with ${failedReport.violations.length} unreviewed matches. Fingerprinted evidence was written without raw match values.`);
    }

    const seed = readJson(args.seed, 'Pinned seed');
    const replay = readJson(args.replayTrace, 'Replay trace');
    const deploymentPolicy = readJson(args.deploymentPolicy, 'Deployment policy');
    if (deploymentPolicy.id !== 'public-demo-readonly') throw new Error('Deployment policy id must be public-demo-readonly.');
    const supportedSeedSchema = deploymentPolicy.compatibility?.seedSchema;
    if (!supportedSeedSchema || !Number.isInteger(supportedSeedSchema.minimum) || !Number.isInteger(supportedSeedSchema.maximum)) {
      throw new Error('Deployment policy compatibility.seedSchema needs integer minimum and maximum values.');
    }
    if (!Number.isInteger(seed.schemaVersion) || seed.schemaVersion < supportedSeedSchema.minimum || seed.schemaVersion > supportedSeedSchema.maximum) {
      throw new Error(`Seed schema ${seed.schemaVersion} is outside deployment compatibility ${supportedSeedSchema.minimum}..${supportedSeedSchema.maximum}.`);
    }

    const humanReview = validateHumanReview(args.humanReview, firstManifest.digest, firstManifest.digest);
    const scrubReport = {
      schemaVersion: SCRUB_REPORT_SCHEMA_VERSION,
      status: humanReview.approved ? 'approved' : 'machine-passed-human-pending',
      generatedAt: new Date().toISOString(),
      seed: {
        schemaVersion: seed.schemaVersion,
        digest: sha256File(args.seed),
        fixedTimeBase: seed.fixedTimeBase,
      },
      generator: { version: GENERATOR_VERSION, digest: sha256File(args.generator) },
      productCompatibility: { seedSchema: supportedSeedSchema },
      replayTrace: { version: readVersion(replay, 'Replay trace'), digest: sha256File(args.replayTrace) },
      generatedDatastore: {
        contentDigest: firstManifest.digest,
        manifestDigest: firstManifest.digest,
        fileCount: firstManifest.fileCount,
        countsByClass: firstManifest.countsByClass,
        scannedRoots: [firstRoot, secondRoot],
      },
      privateSourceTerms: {
        count: sourceTerms.length,
        digest: sha256(sourceTerms.map((term) => term.toLocaleLowerCase('en')).sort().join('\n')),
        shipped: false,
      },
      scanners: {
        categories: ['secret-patterns', 'high-entropy', 'private-keys', 'tokens', 'emails', 'urls', 'ip-addresses', 'absolute-paths', 'user-home-fragments', 'hostnames', 'repository-remotes', 'source-project-names'],
        unreviewedMatchCount: 0,
        allowlist: {
          taskKeyNamespaces: policy.allowedTaskKeyNamespaces ?? [],
          referenceNamespaces: policy.allowedReferenceNamespaces ?? [],
          syntheticUsers: policy.allowedSyntheticUsers ?? [],
          syntheticModels: policy.allowedSyntheticModels ?? [],
          fixtureValues: (policy.allowedValues ?? []).map((item) => ({ category: item.category, derivation: item.derivation })),
        },
        allowedMatches: scan.allowedMatches,
      },
      images: {
        count: scan.images.length,
        sourceScreenshotsShipped: false,
        provenance: 'Every image was freshly created inside each empty generation root by the pinned seed generator. No export, source screenshot, or intermediate redaction input is copied.',
        results: scan.images,
      },
      twoPassProof: {
        recursiveDiff: 'clean',
        differenceCount: 0,
        firstManifestDigest: firstManifest.digest,
        secondManifestDigest: secondManifest.digest,
        identicalManifestDigest: true,
      },
      humanReview,
    };

    const stageName = `agent-studio-demo-${args.release}`;
    const stage = join(work, stageName);
    mkdirSync(join(stage, 'runtime'), { recursive: true });
    cpSync(firstRoot, join(stage, 'runtime', 'datastore'), { recursive: true, dereference: false });
    copyInputs(stage, args);
    const reportPath = join(stage, 'demo-seed-scrub-report.json');
    writeFileSync(reportPath, stableJson(scrubReport));

    const declaredFiles = buildTreeManifest(stage).files;
    const manifest = {
      schemaVersion: RELEASE_SCHEMA_VERSION,
      demoRelease: args.release,
      releaseState: humanReview.approved ? 'approved' : 'candidate',
      productImage: { digest: args.productImage },
      seed: { schemaVersion: seed.schemaVersion, digest: sha256File(args.seed), path: 'inputs/pinned-seed.json' },
      generator: { version: GENERATOR_VERSION, digest: sha256File(args.generator), path: 'inputs/seed-demo-workspace.mjs' },
      replayTrace: { version: readVersion(replay, 'Replay trace'), digest: sha256File(args.replayTrace), path: 'inputs/replay-trace.json' },
      deploymentPolicy: { id: deploymentPolicy.id, digest: sha256File(args.deploymentPolicy), path: 'inputs/deployment-policy.json' },
      scrubReport: { digest: sha256File(reportPath), path: 'demo-seed-scrub-report.json', approved: humanReview.approved },
      compatibility: { seedSchema: supportedSeedSchema },
      runtime: { path: 'runtime', datastorePath: 'runtime/datastore', contentDigest: firstManifest.digest, fileCount: firstManifest.fileCount },
      files: declaredFiles,
    };
    const manifestPath = join(stage, 'demo-release-manifest.json');
    writeFileSync(manifestPath, stableJson(manifest));

    const bundleName = `${stageName}.tar.gz`;
    const bundlePath = join(args.outputDir, bundleName);
    archive(work, stageName, bundlePath);
    copyFileSync(reportPath, join(args.outputDir, 'demo-seed-scrub-report.json'));
    copyFileSync(manifestPath, join(args.outputDir, 'demo-release-manifest.json'));
    writeFileSync(join(args.outputDir, 'SHA256SUMS'), `${sha256File(bundlePath)}  ${bundleName}\n`);
    console.log(stableJson({
      release: args.release,
      releaseState: manifest.releaseState,
      bundle: bundlePath,
      bundleDigest: sha256File(bundlePath),
      manifest: join(args.outputDir, 'demo-release-manifest.json'),
      scrubReport: join(args.outputDir, 'demo-seed-scrub-report.json'),
      generatedContentDigest: firstManifest.digest,
    }).trim());
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

try {
  build(parseArgs(process.argv.slice(2)));
} catch (error) {
  console.error(`demo release build failed: ${error.message}`);
  process.exit(1);
}
