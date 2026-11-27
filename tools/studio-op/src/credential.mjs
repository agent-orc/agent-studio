import fs from 'node:fs';
import path from 'node:path';

/**
 * Credentials come from a named environment variable or an owner-only file.
 * Only file paths, never secret values, are accepted as CLI options.
 */
export const CONFIG_SEGMENTS = ['.config', 'agent-studio'];

/** The two credential files the Owner may write; the config picks one. */
export const CREDENTIAL_FILES = Object.freeze({
  'operator-token': 'api-token',
  'operator-session': 'operator-session',
});

export const DEFAULT_CREDENTIAL = 'operator-token';

/** Compatibility environment variable used by the task-api scripts. */
export const RETIRED_TOKEN_VARIABLE = ['AGENT', 'STUDIO', 'API', 'TOKEN'].join('_');

/**
 * Studio-issued bearer shapes: <prefix>.<id>.<secret>. The prefixes cover API
 * tokens (api), service-account tokens (sat), operator sessions (oas), the
 * poll handle of a terminal sign-in (osp) and the other secrets Studio
 * issues, so a pasted one is masked too.
 */
const TOKEN_SHAPE = /\b(?:api|sat|oas|osp|inv|rnr|enr|ssn|otl)\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{16,}/g;
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
 * Refuse token arguments and unexpected environment carriers. The two named
 * credential variables are consumed and redacted by the CLI.
 */
export function refuseTokenCarriers({ env, argv }) {
  const allowed = new Set([RETIRED_TOKEN_VARIABLE, 'STUDIO_OP_TOKEN']);
  const carrier = Object.entries(env).find(([key, value]) => !allowed.has(key) && value && isTokenShaped(value));
  if (carrier)
    throw new CredentialError('token-environment-refused',
      `The environment variable ${carrier[0]} holds a token-shaped value. Use STUDIO_OP_TOKEN or an owner-only credential file.`);
  if (argv.some(arg => /^--token(?:=|$)/i.test(arg)))
    throw new CredentialError('token-argument-refused',
      'studio-op has no token option. Store the token in its file (studio-op auth status names the path).');
  if (argv.some(isTokenShaped))
    throw new CredentialError('token-argument-refused',
      'A token-shaped value was passed on the command line. Remove it; if it is a real token, ask the Owner to revoke it.');
}

/** A configured tokenFile path, or one of the two standard credential names. */
export function credentialFile(home, config) {
  if (config.tokenFile) return { name: 'custom', file: path.resolve(config.tokenFile), kind: 'api-token' };
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
  if (!info.exists && (config.credential !== undefined || config.tokenFile !== undefined))
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
