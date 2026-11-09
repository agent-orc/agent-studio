import fs from 'node:fs';
import path from 'node:path';

/**
 * Guardrail "Tokens live only in files" (Dossier operator-agent-level): the
 * token comes only from one file with owner-only permissions under
 * ~/.config/agent-studio. There is no environment variable, no command-line
 * option and no fallback between files.
 */
export const CONFIG_SEGMENTS = ['.config', 'agent-studio'];

/** The two credential files the Owner may write; the config picks one. */
export const CREDENTIAL_FILES = Object.freeze({
  'operator-token': 'api-token',
  'operator-session': 'operator-session',
});

export const DEFAULT_CREDENTIAL = 'operator-token';

/** The variable older task-api scripts read. Its presence is refused, never used. */
export const RETIRED_TOKEN_VARIABLE = ['AGENT', 'STUDIO', 'API', 'TOKEN'].join('_');

/**
 * Studio-issued bearer shapes: <prefix>.<id>.<secret>. The prefixes cover API
 * tokens (api), service-account tokens (sat), operator sessions (oas) and the
 * other secrets Studio issues, so a pasted one is masked too.
 */
const TOKEN_SHAPE = /\b(?:api|sat|oas|inv|rnr|enr|ssn|otl)\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{16,}/g;
const BEARER_VALUE = /\b(Bearer\s+)[^\s"',;}]+/gi;
export const REDACTED = '[redacted-token]';

/** A refusal with a stable code; its message never contains the token. */
export class CredentialError extends Error {
  constructor(code, message) {
    super(message);
    this.name = 'CredentialError';
    this.code = code;
  }
}

export function configDirectory(home) {
  return path.join(home, ...CONFIG_SEGMENTS);
}

export function isTokenShaped(text) {
  return new RegExp(TOKEN_SHAPE.source).test(String(text));
}

/** Replaces the token, any token-shaped value and bearer header values. */
export function redact(text, token = null) {
  let value = String(text);
  if (token) value = value.split(token).join(REDACTED);
  return value.replace(TOKEN_SHAPE, REDACTED).replace(BEARER_VALUE, `$1${REDACTED}`);
}

/**
 * Refuses every place a token must not come from: the retired variable, any
 * environment value shaped like a Studio token, a `--token` option, and a
 * token-shaped command-line argument.
 */
export function refuseTokenCarriers({ env, argv }) {
  if (env[RETIRED_TOKEN_VARIABLE] !== undefined)
    throw new CredentialError('token-environment-refused',
      `${RETIRED_TOKEN_VARIABLE} is not read. Unset it and keep the token only in the token file.`);
  const carrier = Object.entries(env).find(([, value]) => value && isTokenShaped(value));
  if (carrier)
    throw new CredentialError('token-environment-refused',
      `The environment variable ${carrier[0]} holds a token-shaped value. Unset it; studio-op reads the token only from its file.`);
  if (argv.some(arg => /^--token(?:-[a-z]+)?(?:=|$)/i.test(arg)))
    throw new CredentialError('token-argument-refused',
      'studio-op has no token option. Store the token in its file (studio-op auth status names the path).');
  if (argv.some(isTokenShaped))
    throw new CredentialError('token-argument-refused',
      'A token-shaped value was passed on the command line. Remove it; if it is a real token, ask the Owner to revoke it.');
}

/** The file the config selects; only the two known names are allowed. */
export function credentialFile(home, config) {
  const name = config.credential ?? DEFAULT_CREDENTIAL;
  if (!Object.hasOwn(CREDENTIAL_FILES, name))
    throw new CredentialError('credential-name-invalid',
      `The config value "credential" must be ${Object.keys(CREDENTIAL_FILES).join(' or ')}.`);
  return { name, file: path.join(configDirectory(home), name), kind: CREDENTIAL_FILES[name] };
}

/**
 * Inspects the selected credential file without reading the secret. Used by
 * `auth status` and before every read.
 */
export function inspectCredential(home, config) {
  const selected = credentialFile(home, config);
  let stat;
  try { stat = fs.lstatSync(selected.file); }
  catch (error) {
    if (error.code === 'ENOENT') return { ...selected, exists: false };
    throw new CredentialError('token-file-unreadable', `Cannot read ${selected.file} (${error.code}).`);
  }
  const mode = stat.mode & 0o777;
  return { ...selected, exists: true, regular: stat.isFile(), mode: mode.toString(8).padStart(3, '0'),
    ownerOnly: process.platform === 'win32' || (mode & 0o077) === 0,
    ownedByUser: typeof process.getuid !== 'function' || stat.uid === process.getuid() };
}

/**
 * Reads the selected credential file. Returns null when it does not exist.
 * Refuses a file that is not a regular file of the current user, or that
 * group or others may read, write, or execute.
 */
export function readCredential({ home, config }) {
  const info = inspectCredential(home, config);
  if (!info.exists && config.credential !== undefined)
    throw new CredentialError('token-file-missing', `The config selects ${info.file}, which does not exist.`);
  if (!info.exists) return null;
  if (!info.regular)
    throw new CredentialError('token-file-not-regular', `${info.file} must be a regular file, not a link or directory.`);
  // Windows keeps access in ACLs, not mode bits; the user profile is owner-only by default.
  if (!info.ownerOnly)
    throw new CredentialError('token-file-permissions',
      `${info.file} is accessible to group or others (mode ${info.mode}). Run: chmod 600 ${info.file}`);
  if (!info.ownedByUser)
    throw new CredentialError('token-file-owner', `${info.file} must belong to the user who runs studio-op.`);
  const token = fs.readFileSync(info.file, 'utf8').trim();
  if (!token) throw new CredentialError('token-file-empty', `${info.file} is empty.`);
  if (/\s/.test(token)) throw new CredentialError('token-file-invalid', `${info.file} must hold exactly one token.`);
  return { token, file: info.file, kind: info.kind, name: info.name };
}

/** The public parts of a token: its kind prefix and id. Never the secret. */
export function describeToken(token) {
  const [prefix, id] = String(token).split('.');
  return { prefix: prefix ?? null, id: /^[0-9a-f]{32}$/.test(id ?? '') ? id : null };
}
