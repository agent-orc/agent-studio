import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import os from 'node:os';
import path from 'node:path';

/**
 * The only place a task-api script reads a Studio API token from (Dossier
 * operator-agent-level, OP-1). There is no environment variable and no
 * command-line option for the token, and no fallback.
 */
export const TOKEN_FILE_SEGMENTS = ['.config', 'agent-studio', 'operator-token'];

/** The variable older scripts read. Its presence is refused, never used. */
export const RETIRED_TOKEN_VARIABLE = 'AGENT_STUDIO_API_TOKEN';

const TOKEN_SHAPE = /\b(?:api|sat|oas)\.[A-Za-z0-9]{8,}\.[A-Za-z0-9_-]{16,}/g;
const REDACTED = '[redacted-token]';

/** A refusal with a stable code; its message never contains the token. */
export class OperatorTokenError extends Error {
  constructor(code, message) {
    super(`${code}: ${message}`);
    this.name = 'OperatorTokenError';
    this.code = code;
  }
}

export function tokenFilePath() {
  return path.join(os.homedir(), ...TOKEN_FILE_SEGMENTS);
}

/**
 * Reads the token file. Returns null when it does not exist. Refuses a file
 * that is not a regular file of the current user, or that group or others
 * may read, write, or execute.
 */
export function readOperatorToken() {
  const file = tokenFilePath();
  let stat;
  try { stat = fs.lstatSync(file); }
  catch (error) {
    if (error.code === 'ENOENT') return null;
    throw new OperatorTokenError('token-file-unreadable', `Cannot read ${file} (${error.code}).`);
  }
  if (!stat.isFile())
    throw new OperatorTokenError('token-file-not-regular', `${file} must be a regular file, not a link or directory.`);
  // Windows keeps access in ACLs, not mode bits; the user profile is owner-only by default.
  if (process.platform !== 'win32') {
    if ((stat.mode & 0o077) !== 0)
      throw new OperatorTokenError('token-file-permissions',
        `${file} is accessible to group or others (mode ${(stat.mode & 0o777).toString(8)}). Run: chmod 600 ${file}`);
    if (typeof process.getuid === 'function' && stat.uid !== process.getuid())
      throw new OperatorTokenError('token-file-owner', `${file} must belong to the user who runs the script.`);
  }
  const token = fs.readFileSync(file, 'utf8').trim();
  if (!token) throw new OperatorTokenError('token-file-empty', `${file} is empty.`);
  if (/\s/.test(token)) throw new OperatorTokenError('token-file-invalid', `${file} must hold exactly one token.`);
  return token;
}

/** Replaces the token and any token-shaped value in text meant for output. */
export function redact(text, token = null) {
  let value = String(text);
  if (token) value = value.split(token).join(REDACTED);
  return value.replace(TOKEN_SHAPE, REDACTED);
}

let guardedToken = null;

/**
 * Once a token is loaded, everything this process writes to stdout or stderr
 * passes through {@link redact}, so no script output carries it.
 */
function guardOutput(token) {
  if (guardedToken === token) return;
  const first = guardedToken === null;
  guardedToken = token;
  if (!first) return;
  for (const stream of [process.stdout, process.stderr]) {
    const write = stream.write.bind(stream);
    // Binary chunks (a test runner's protocol, for example) pass unchanged
    // unless they carry the token itself.
    stream.write = (chunk, ...rest) => write(
      typeof chunk === 'string' ? redact(chunk, guardedToken)
        : Buffer.isBuffer(chunk) && chunk.includes(guardedToken) ? redact(chunk.toString(), guardedToken)
          : chunk, ...rest);
  }
}

function refuseCarriers(token) {
  if (process.env[RETIRED_TOKEN_VARIABLE] !== undefined)
    throw new OperatorTokenError('token-environment-refused',
      `${RETIRED_TOKEN_VARIABLE} is no longer read. Unset it and store the token in ${tokenFilePath()} (chmod 600).`);
  const argv = process.argv.slice(2);
  if (argv.some(arg => (token && arg.includes(token)) || new RegExp(TOKEN_SHAPE.source).test(arg)))
    throw new OperatorTokenError('token-argument-refused',
      `A token was passed as a command-line argument. Store it in ${tokenFilePath()} instead.`);
}

function isLoopback(hostname) {
  return hostname === 'localhost' || hostname === '::1' || hostname === '[::1]' || /^127\./.test(hostname);
}

/**
 * Local requests keep their client attribution. With a token file, every
 * request also carries the bearer; an HTTPS origin needs one.
 */
export function taskApiRequest(options, callback) {
  const base = process.env.TASKBOARD_URL
    ?? `http://${options.hostname}:${options.port}`;
  const target = new URL(options.path, base);
  if (target.protocol !== 'http:' && target.protocol !== 'https:')
    throw new Error('TASKBOARD_URL must use HTTP or HTTPS.');
  refuseCarriers(null);
  const token = readOperatorToken();
  if (token) {
    guardOutput(token);
    refuseCarriers(token);
    if (target.protocol === 'http:' && !isLoopback(target.hostname))
      throw new OperatorTokenError('token-requires-https', 'A token is sent only over HTTPS or to a loopback address.');
  } else if (target.protocol === 'https:') {
    throw new OperatorTokenError('token-file-missing',
      `A networked Studio needs an API token in ${tokenFilePath()} (chmod 600).`);
  }
  const headers = { ...options.headers, ...(token ? { Authorization: `Bearer ${token}` } : {}) };
  return (target.protocol === 'https:' ? https : http).request(target,
    { method: options.method, headers }, callback);
}
