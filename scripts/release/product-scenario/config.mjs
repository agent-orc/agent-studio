// Arguments and sign-in file of the product scenario. Only the Owner's sign-in
// comes from a file; the invited people get one-time links and passwords that
// the run generates and keeps in memory. There is no password option and no
// environment variable, the same guardrail studio-op applies to tokens.
import fs from 'node:fs';
import path from 'node:path';
import { parseArgs } from 'node:util';

import { refuseTokenCarriers } from '../../../tools/studio-op/src/credential.mjs';

export const SCENARIO = Object.freeze({
  id: 'product',
  description: 'A product manager, a designer and a manager evolve a live website through Studio',
});

export const DEFAULTS = Object.freeze({
  platform: 'operator',
  project: 'Click Counter',
  loginPrefix: 'product-scenario',
  wish: 'A reset confirmation and a history of the last ten values',
  location: 'Next to the counter: ask before the reset clears the count, and list the last ten values below it',
  example: 'A small confirmation dialog with "Reset" and "Cancel"; the history as a plain numbered list, newest first',
  feedback: 'secondary style, smaller',
  codingTimeoutMinutes: 240,
  fixTimeoutMinutes: 120,
  releaseTimeoutMinutes: 60,
  deployTimeoutMinutes: 20,
  pollSeconds: 15,
  teardown: 'after',
});

/**
 * The three simulated people. All hold the Product role, whose binding role id
 * is `contributor` (IamCatalog; "Product" is its display name). The Owner
 * grants the production approval of the project to the manager alone.
 */
export const PEOPLE = Object.freeze([
  Object.freeze({ id: 'pm', title: 'Product manager', displayName: 'Pia Product', productionApproval: false }),
  Object.freeze({ id: 'designer', title: 'Designer', displayName: 'Dana Design', productionApproval: false }),
  Object.freeze({ id: 'manager', title: 'Manager', displayName: 'Max Manager', productionApproval: true }),
]);

export const PRODUCT_ROLE = 'contributor';

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
    throw new UsageError('There is no password option. Put the Owner sign-in in a file given with --user-file.');
  let values;
  try {
    ({ values } = parseArgs({
      args: argv,
      options: {
        'studio-url': { type: 'string' },
        platform: { type: 'string', default: DEFAULTS.platform },
        project: { type: 'string', default: DEFAULTS.project },
        'login-prefix': { type: 'string', default: DEFAULTS.loginPrefix },
        'user-file': { type: 'string' },
        wish: { type: 'string', default: DEFAULTS.wish },
        feedback: { type: 'string', default: DEFAULTS.feedback },
        'coding-timeout-minutes': { type: 'string', default: String(DEFAULTS.codingTimeoutMinutes) },
        'fix-timeout-minutes': { type: 'string', default: String(DEFAULTS.fixTimeoutMinutes) },
        'release-timeout-minutes': { type: 'string', default: String(DEFAULTS.releaseTimeoutMinutes) },
        'deploy-timeout-minutes': { type: 'string', default: String(DEFAULTS.deployTimeoutMinutes) },
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
  if (!/^[a-z][a-z0-9-]{2,23}$/.test(values['login-prefix']))
    throw new UsageError('--login-prefix must be 3 to 24 lowercase letters, digits or hyphens.');
  return {
    help: false,
    studioUrl: values['studio-url'],
    platform: values.platform,
    project: values.project,
    loginPrefix: values['login-prefix'],
    userFile: values['user-file'],
    wish: values.wish,
    location: DEFAULTS.location,
    example: DEFAULTS.example,
    feedback: values.feedback,
    codingTimeoutMs: positiveNumber(values['coding-timeout-minutes'], '--coding-timeout-minutes') * 60_000,
    fixTimeoutMs: positiveNumber(values['fix-timeout-minutes'], '--fix-timeout-minutes') * 60_000,
    releaseTimeoutMs: positiveNumber(values['release-timeout-minutes'], '--release-timeout-minutes') * 60_000,
    deployTimeoutMs: positiveNumber(values['deploy-timeout-minutes'], '--deploy-timeout-minutes') * 60_000,
    pollMs: positiveNumber(values['poll-seconds'], '--poll-seconds') * 1000,
    teardown: values.teardown,
    reportDir: path.resolve(values['report-dir'] ?? env.JOB_RESULTS_DIR ?? 'artifacts/product-scenario'),
  };
}

/**
 * The login of one simulated person in one run: `<prefix>-<person>-<runId>`.
 * Studio cannot delete a user, only disable it, so each run invites new logins
 * and setup disables what an earlier run left enabled.
 */
export function personLogin(config, person, runId) {
  return `${config.loginPrefix}-${person.id}-${runId}`;
}

/**
 * Reads the Owner sign-in file: JSON { "login": "...", "password": "...",
 * "totpSecret"?: "<base32>" }. Refuses a file that is a link, belongs to
 * another user, or that group or others may access, like the studio-op token file.
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

export const USAGE = `Usage: node scripts/release/product-scenario.mjs --studio-url URL --user-file PATH [options]

Plays the product scenario (AGT-3183) against a real installation on which the
lifecycle scenario (AGT-3178) left "Click Counter" running on stage and
production (run it with --teardown never). Writes product.json, product.html
and screenshots to the report directory.

  --studio-url URL            Studio origin, for example https://studio.v1.internal
  --user-file PATH            Owner sign-in, JSON {login,password,totpSecret?}, mode 600
  --project NAME              Project to evolve (default "${DEFAULTS.project}")
  --login-prefix TEXT         Logins of the invited people: <prefix>-pm-<run>, -designer-<run>,
                              -manager-<run> (default ${DEFAULTS.loginPrefix})
  --wish TEXT                 The product manager's wish (default "${DEFAULTS.wish}")
  --feedback TEXT             The designer's comment on the reset button (default "${DEFAULTS.feedback}")
  --platform NAME             Platform label in the report (default ${DEFAULTS.platform})
  --coding-timeout-minutes N  Wait for the first delivery and its preview (default ${DEFAULTS.codingTimeoutMinutes})
  --fix-timeout-minutes N     Wait for the fix round and the updated preview (default ${DEFAULTS.fixTimeoutMinutes})
  --release-timeout-minutes N Wait for gate, release and stage after acceptance (default ${DEFAULTS.releaseTimeoutMinutes})
  --deploy-timeout-minutes N  Wait for the production deployment (default ${DEFAULTS.deployTimeoutMinutes})
  --poll-seconds N            Poll interval (default ${DEFAULTS.pollSeconds})
  --teardown after|never|only Disable the invited people, remove their grants and the Project URL
  --report-dir PATH           Defaults to JOB_RESULTS_DIR or artifacts/product-scenario
`;

function positiveNumber(text, name) {
  const value = Number(text);
  if (!Number.isFinite(value) || value <= 0) throw new UsageError(`${name} must be a positive number.`);
  return value;
}
