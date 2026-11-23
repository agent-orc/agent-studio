import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { usage } from './args.mjs';
import { CredentialError } from './credential.mjs';
import { EXIT } from './exit-codes.mjs';
import { renderQr } from './qr.mjs';
import { OperationError, failure } from './studio.mjs';

/**
 * `studio-op login` (Dossier operator-elevated-session, ES-3, decision ES-D2
 * option S2): a terminal-started sign-in that the Owner confirms in Studio
 * with a fresh second factor. The terminal never sees a password, a factor or
 * a secret it would have to copy; the session it receives goes straight into
 * the operator-session file with mode 600 and is never printed.
 */
export const SESSION_FILE = 'operator-session';
/**
 * The device key until ES-4 moves it into the memory of a resident studio-op
 * agent and ES-5 into the TPM or Secure Enclave. It sits beside the session
 * file with the same mode; Studio stores its thumbprint but does not yet
 * require proofs signed with it.
 */
export const DEVICE_KEY_FILE = 'operator-device-key.json';
export const START_PATH = '/api/auth/operator-sign-in';
export const POLL_PATH = '/api/auth/operator-sign-in/poll';
const MAX_LABEL_LENGTH = 64;
const UNREACHABLE_RETRIES = 5;

const base64Url = buffer => Buffer.from(buffer).toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');

/** RFC 7638 thumbprint of an EC public JWK, the value Studio shows on the confirmation page. */
export function keyThumbprint(jwk) {
  const canonical = `{"crv":"${jwk.crv}","kty":"${jwk.kty}","x":"${jwk.x}","y":"${jwk.y}"}`;
  return base64Url(crypto.createHash('sha256').update(canonical).digest());
}

function ownerOnly(file) {
  const stat = fs.lstatSync(file);
  if (!stat.isFile()) throw new CredentialError('device-key-not-regular', `${file} must be a regular file, not a link or directory.`);
  if (process.platform !== 'win32' && (stat.mode & 0o077) !== 0)
    throw new CredentialError('device-key-permissions',
      `${file} is accessible to group or others (mode ${(stat.mode & 0o777).toString(8)}). Run: chmod 600 ${file}`);
  if (typeof process.getuid === 'function' && stat.uid !== process.getuid())
    throw new CredentialError('device-key-owner', `${file} must belong to the user who runs studio-op.`);
}

/** Writes a file with mode 600 through a temporary file and a rename, so no reader sees a partial or open file. */
export function writePrivateFile(file, text) {
  fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
  const temporary = `${file}.${process.pid}.${crypto.randomBytes(4).toString('hex')}.tmp`;
  fs.writeFileSync(temporary, text, { mode: 0o600, flag: 'wx' });
  try {
    fs.chmodSync(temporary, 0o600);
    fs.renameSync(temporary, file);
  } catch (error) {
    fs.rmSync(temporary, { force: true });
    throw error;
  }
}

/** Loads the device key, or creates a P-256 key the first time. Returns its public JWK and thumbprint. */
export function deviceKey(configDir) {
  const file = path.join(configDir, DEVICE_KEY_FILE);
  let created = false;
  let privateKey;
  if (fs.existsSync(file)) {
    ownerOnly(file);
    try { privateKey = crypto.createPrivateKey({ key: JSON.parse(fs.readFileSync(file, 'utf8')), format: 'jwk' }); }
    catch { throw new CredentialError('device-key-invalid', `${file} does not hold a P-256 private key. Remove it to create a new device key.`); }
    if (privateKey.asymmetricKeyDetails?.namedCurve !== 'prime256v1')
      throw new CredentialError('device-key-invalid', `${file} does not hold a P-256 private key. Remove it to create a new device key.`);
  } else {
    ({ privateKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' }));
    writePrivateFile(file, `${JSON.stringify(privateKey.export({ format: 'jwk' }))}\n`);
    created = true;
  }
  const { kty, crv, x, y } = crypto.createPublicKey(privateKey).export({ format: 'jwk' });
  const publicJwk = { kty, crv, x, y };
  return { file, created, publicJwk, thumbprint: keyThumbprint(publicJwk) };
}

/** The label the Owner recognises: OS user and machine name. */
export function defaultDeviceLabel() {
  let user = 'user';
  try { user = os.userInfo().username || user; } catch { /* no passwd entry */ }
  return `${user}@${os.hostname() || 'machine'}`.slice(0, MAX_LABEL_LENGTH);
}

/** Points studio-op.json at the session file, keeping the other keys. */
function selectSessionCredential(configDir, config) {
  if (config.credential === SESSION_FILE) return false;
  writePrivateFile(path.join(configDir, 'studio-op.json'), `${JSON.stringify({ ...config, credential: SESSION_FILE }, null, 2)}\n`);
  return true;
}

const sleep = (ms, signal) => new Promise(resolve => {
  const timer = setTimeout(resolve, ms);
  signal?.addEventListener('abort', () => { clearTimeout(timer); resolve(); }, { once: true });
});

/**
 * Runs the sign-in: start, print the code, the link and a QR code, poll at
 * the interval Studio names until the Owner confirms or denies or the code
 * expires, then write the session file.
 */
export async function login(ctx, options, { wait = sleep, now = () => Date.now() } = {}) {
  const label = (options.label ?? defaultDeviceLabel()).trim();
  if (!label || label.length > MAX_LABEL_LENGTH || /\p{Cc}/u.test(label))
    throw usage('invalid-option', `--label must be 1 to ${MAX_LABEL_LENGTH} printable characters.`);
  const key = deviceKey(ctx.configDir);
  if (key.created) ctx.output.note(`Created the device key ${key.file} (mode 600).`);

  const started = await ctx.client.request('POST', START_PATH, { body: {
    publicKey: key.publicJwk, deviceLabel: label, operatorAgent: options.agent ?? null, classes: options.class ?? [],
  } });
  ctx.output.note([
    '',
    `Confirm this sign-in in Studio as the Owner, with your second factor.`,
    '',
    `  Code:   ${started.userCode}`,
    `  Open:   ${started.verificationUriComplete}`,
    `  Or go to ${started.verificationUri} and enter the code.`,
    `  Device: ${label}, key ${key.thumbprint}`,
    '',
    ...(options.noQr ? [] : [renderQr(started.verificationUriComplete), '']),
    `Waiting for the confirmation (the code expires at ${started.expiresAt})...`,
  ].join('\n'));

  let interval = Math.max(1, Number(started.interval) || 5);
  const deadline = Date.parse(started.expiresAt) + 30_000;
  let unreachable = 0;
  for (;;) {
    await wait(interval * 1000);
    if (now() > deadline)
      throw new OperationError('sign-in-expired', 'The code expired before the Owner confirmed it. Run studio-op login again.',
        { exitCode: EXIT.SIGN_IN_EXPIRED });
    let answer;
    try { answer = await ctx.client.send('POST', POLL_PATH, { body: { pollHandle: started.pollHandle } }); }
    catch (error) {
      if (error.code !== 'studio-unreachable' || ++unreachable > UNREACHABLE_RETRIES) throw error;
      ctx.output.note(`Studio did not answer; trying again in ${interval} s.`);
      continue;
    }
    unreachable = 0;
    if (answer.status < 200 || answer.status >= 300) throw failure('POST', POLL_PATH, answer);
    const body = answer.body ?? {};
    switch (body.status) {
      case 'pending':
        continue;
      case 'slow-down':
        // Studio names the raised interval (RFC 8628: five seconds more each time).
        interval = Math.max(interval, Number(body.interval) || interval + 5);
        continue;
      case 'denied':
        throw new OperationError('sign-in-denied', body.message ?? 'The Owner denied this sign-in.', { exitCode: EXIT.SIGN_IN_DENIED });
      case 'expired':
        throw new OperationError('sign-in-expired', body.message ?? 'The code expired. Run studio-op login again.',
          { exitCode: EXIT.SIGN_IN_EXPIRED });
      case 'session': {
        if (typeof body.token !== 'string' || !body.token.startsWith('oas.'))
          throw new OperationError('sign-in-invalid-answer', 'Studio answered without an operator session.', { exitCode: EXIT.FAILURE });
        // From here on every line studio-op writes masks the new token.
        ctx.output.protect(body.token);
        const file = path.join(ctx.configDir, SESSION_FILE);
        writePrivateFile(file, `${body.token}\n`);
        const configUpdated = selectSessionCredential(ctx.configDir, ctx.config);
        const data = {
          operatorAgentId: body.operatorAgentId, sessionId: body.sessionId, actor: body.actor, delegatedBy: body.delegatedBy,
          expiresAt: body.expiresAt, classes: body.classes ?? [], keyThumbprint: body.keyThumbprint ?? key.thumbprint,
          deviceLabel: label, file, mode: '600', configUpdated,
        };
        return { data, text: [
          `Signed in as ${body.actor}, delegated by ${body.delegatedBy}, until ${body.expiresAt}.`,
          `Session written to ${file} (mode 600); the token is not shown.`,
          configUpdated ? 'studio-op.json now selects the operator-session file.' : null,
          (body.classes ?? []).length ? `Action classes recorded: ${body.classes.join(', ')}.` : null,
          'Check it with: studio-op auth status',
        ].filter(Boolean).join('\n') };
      }
      default:
        throw new OperationError('sign-in-invalid-answer', `Studio answered the poll with "${body.status}".`, { exitCode: EXIT.FAILURE });
    }
  }
}
