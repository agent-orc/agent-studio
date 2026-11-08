// Arguments and sign-in files of the lifecycle scenario. Passwords come only
// from owner-only files; there is no password option and no environment
// variable, the same guardrail studio-op applies to tokens.
import fs from 'node:fs';
import path from 'node:path';
import { parseArgs } from 'node:util';

import { refuseTokenCarriers } from '../../../tools/studio-op/src/credential.mjs';

export const SCENARIO = Object.freeze({
  id: 'lifecycle',
  description: 'A simulated user takes a new project from creation to production inside a quota',
});

export const DEFAULTS = Object.freeze({
  platform: 'operator',
  workspace: 'Department demo',
  project: 'Click Counter',
  template: 'static-web',
  wish: 'A counter with reset and a history of the last ten values',
  quotaModelTokens: 2_000_000,
  quotaCoreHours: 40,
  codingTimeoutMinutes: 240,
  deployTimeoutMinutes: 20,
  releaseTimeoutMinutes: 60,
  pollSeconds: 15,
  teardown: 'after',
});

const TEARDOWN_MODES = new Set(['after', 'never', 'only']);

export class UsageError extends Error {
  constructor(message) {
    super(message);
    this.name = 'UsageError';
  }
}

export function parseConfig(argv, env) {
  refuseTokenCarriers({ env, argv });
  if (argv.some(arg => /^--password(?:=|$)/i.test(arg)))
    throw new UsageError('There is no password option. Put the sign-in in a file given with --user-file.');
  let values;
  try {
    ({ values } = parseArgs({
      args: argv,
      options: {
        'studio-url': { type: 'string' },
        platform: { type: 'string', default: DEFAULTS.platform },
        'stage-host': { type: 'string' },
        'production-host': { type: 'string' },
        'stage-address': { type: 'string' },
        'production-address': { type: 'string' },
        'stage-directory': { type: 'string' },
        'production-directory': { type: 'string' },
        'repository-owner': { type: 'string' },
        workspace: { type: 'string', default: DEFAULTS.workspace },
        project: { type: 'string', default: DEFAULTS.project },
        template: { type: 'string', default: DEFAULTS.template },
        wish: { type: 'string', default: DEFAULTS.wish },
        'user-file': { type: 'string' },
        'quota-tokens': { type: 'string', default: String(DEFAULTS.quotaModelTokens) },
        'quota-core-hours': { type: 'string', default: String(DEFAULTS.quotaCoreHours) },
        'coding-timeout-minutes': { type: 'string', default: String(DEFAULTS.codingTimeoutMinutes) },
        'deploy-timeout-minutes': { type: 'string', default: String(DEFAULTS.deployTimeoutMinutes) },
        'release-timeout-minutes': { type: 'string', default: String(DEFAULTS.releaseTimeoutMinutes) },
        'poll-seconds': { type: 'string', default: String(DEFAULTS.pollSeconds) },
        teardown: { type: 'string', default: DEFAULTS.teardown },
        'report-dir': { type: 'string' },
        help: { type: 'boolean', default: false },
      },
      strict: true,
    }));
  } catch (error) {
    throw new UsageError(error.message);
  }
  if (values.help) return { help: true };
  if (!values['studio-url']) throw new UsageError('--studio-url is required.');
  if (!values['user-file']) throw new UsageError('--user-file is required.');
  if (!TEARDOWN_MODES.has(values.teardown))
    throw new UsageError(`--teardown must be one of ${[...TEARDOWN_MODES].join(', ')}.`);
  const playing = values.teardown !== 'only';
  for (const name of ['stage-host', 'stage-address', 'production-address', 'repository-owner'])
    if (playing && !values[name])
      throw new UsageError(`--${name} is required. Stage and production run on the host of Dossier application-lifecycle D3.`);
  const slug = projectSlug(values.project);
  const stageHost = values['stage-host'] ?? null;
  return {
    help: false,
    studioUrl: values['studio-url'],
    platform: values.platform,
    slug,
    repositoryOwner: values['repository-owner'] ?? null,
    stage: playing ? {
      host: stageHost,
      address: httpUrl(values['stage-address'], '--stage-address'),
      directory: values['stage-directory'] ?? `/srv/agent-studio/sites/${slug}-stage`,
    } : null,
    production: playing ? {
      host: values['production-host'] ?? stageHost,
      address: httpUrl(values['production-address'], '--production-address'),
      directory: values['production-directory'] ?? `/srv/agent-studio/sites/${slug}-production`,
    } : null,
    workspace: values.workspace,
    project: values.project,
    template: values.template,
    wish: values.wish,
    userFile: values['user-file'],
    quota: {
      modelTokensPerWeek: positiveInteger(values['quota-tokens'], '--quota-tokens'),
      computeCoreHoursPerWeek: positiveNumber(values['quota-core-hours'], '--quota-core-hours'),
    },
    codingTimeoutMs: positiveNumber(values['coding-timeout-minutes'], '--coding-timeout-minutes') * 60_000,
    deployTimeoutMs: positiveNumber(values['deploy-timeout-minutes'], '--deploy-timeout-minutes') * 60_000,
    releaseTimeoutMs: positiveNumber(values['release-timeout-minutes'], '--release-timeout-minutes') * 60_000,
    pollMs: positiveNumber(values['poll-seconds'], '--poll-seconds') * 1000,
    teardown: values.teardown,
    reportDir: path.resolve(values['report-dir'] ?? env.JOB_RESULTS_DIR ?? 'artifacts/lifecycle-scenario'),
  };
}

/**
 * Reads a sign-in file: JSON { "login": "...", "password": "...", "totpSecret"?:
 * "<base32>" } (the secret only when the Owner enrolled an authenticator). Refuses a
 * file that is a link, belongs to another user, or that group or others may
 * access, like the studio-op token file.
 */
export function readSignIn(file) {
  let stat;
  try { stat = fs.lstatSync(file); }
  catch (error) { throw new UsageError(`Cannot read the sign-in file ${file} (${error.code}).`); }
  if (!stat.isFile()) throw new UsageError(`${file} must be a regular file, not a link or directory.`);
  if (process.platform !== 'win32' && (stat.mode & 0o077) !== 0)
    throw new UsageError(`${file} is accessible to group or others. Run: chmod 600 ${file}`);
  if (typeof process.getuid === 'function' && stat.uid !== process.getuid())
    throw new UsageError(`${file} must belong to the user who runs the scenario.`);
  let parsed;
  try { parsed = JSON.parse(fs.readFileSync(file, 'utf8')); }
  catch { throw new UsageError(`${file} must hold JSON with "login" and "password".`); }
  if (typeof parsed?.login !== 'string' || !parsed.login || typeof parsed?.password !== 'string' || !parsed.password)
    throw new UsageError(`${file} must hold JSON with "login" and "password".`);
  if (parsed.totpSecret !== undefined && (typeof parsed.totpSecret !== 'string' || !/^[A-Z2-7=\s]+$/i.test(parsed.totpSecret)))
    throw new UsageError(`${file}: "totpSecret" must be the base32 authenticator secret.`);
  return { login: parsed.login, password: parsed.password, ...(parsed.totpSecret ? { totpSecret: parsed.totpSecret } : {}) };
}

export const USAGE = `Usage: node scripts/release/lifecycle-scenario.mjs --studio-url URL --user-file PATH
         --repository-owner ORG --stage-host HOST --stage-address URL --production-address URL [options]

Plays the application-lifecycle first run (AGT-3178) against a real installation
and writes lifecycle.json, lifecycle.html and screenshots to the report directory.

  --studio-url URL            Studio origin, for example https://studio.v1.internal
  --repository-owner ORG      GitHub test organisation for the new repository
  --stage-host HOST           Host registry name that runs stage (Dossier D3: V1)
  --production-host HOST      Host for production; defaults to --stage-host
  --stage-address URL         Internal address of stage, for example http://v1.internal:8081
  --production-address URL    Internal address of production
  --stage-directory PATH      Served release directory on the host (default /srv/agent-studio/sites/<slug>-stage)
  --production-directory PATH Served release directory of production (default .../<slug>-production)
  --platform NAME             Platform label in the report (default ${DEFAULTS.platform})
  --workspace NAME            Workspace to create (default "${DEFAULTS.workspace}")
  --project NAME              Project to create (default "${DEFAULTS.project}")
  --template ID               Project template (default ${DEFAULTS.template})
  --wish TEXT                 Orchestrator chat wish
  --user-file PATH            Sign-in of the simulated user, JSON {login,password,totpSecret?}, mode 600.
                              The user must be the Owner: template projects and quota
                              limits are Owner actions on a networked installation.
  --quota-tokens N            Weekly model-token limit (default ${DEFAULTS.quotaModelTokens})
  --quota-core-hours N        Weekly core-hour limit (default ${DEFAULTS.quotaCoreHours})
  --coding-timeout-minutes N  Wait for cards to integrate (default ${DEFAULTS.codingTimeoutMinutes})
  --deploy-timeout-minutes N  Wait for one production deployment (default ${DEFAULTS.deployTimeoutMinutes})
  --release-timeout-minutes N Wait for gate, release and stage after acceptance (default ${DEFAULTS.releaseTimeoutMinutes})
  --poll-seconds N            Poll interval (default ${DEFAULTS.pollSeconds})
  --teardown after|never|only Remove workspace, project, repository and environments
  --report-dir PATH           Defaults to JOB_RESULTS_DIR or artifacts/lifecycle-scenario
`;

/** The project slug Studio uses in hash routes (toProjectSlug in project-shell.config.ts). */
export function projectSlug(name) {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function httpUrl(text, name) {
  let url;
  try { url = new URL(text); } catch { throw new UsageError(`${name} must be an http or https URL.`); }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new UsageError(`${name} must be an http or https URL.`);
  return url.href.replace(/\/$/, '');
}

function positiveNumber(text, name) {
  const value = Number(text);
  if (!Number.isFinite(value) || value <= 0) throw new UsageError(`${name} must be a positive number.`);
  return value;
}

function positiveInteger(text, name) {
  const value = positiveNumber(text, name);
  if (!Number.isInteger(value)) throw new UsageError(`${name} must be a whole number.`);
  return value;
}
