import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { usage } from './args.mjs';
import { EXIT } from './exit-codes.mjs';
import { openDeviceKey, certifySession } from './device-key.mjs';
import { renderQr } from './qr.mjs';
import { AGENT_FILE, agentCall, startAgent, stopAgent, digest } from './agent.mjs';
import { OperationError, failure } from './studio.mjs';

export { keyThumbprint } from './agent.mjs';

/**
 * `studio-op login` (Dossier operator-elevated-session, ES-3, decision ES-D2
 * option S2): a terminal-started sign-in that the Owner confirms in Studio
 * with a fresh second factor. The terminal never sees a password, a factor or
 * a secret it would have to copy; the session it receives goes straight into
 * the operator-session file with mode 600 and is never printed.
 */
export const SESSION_FILE = 'operator-session';
export const START_PATH = '/api/auth/operator-sign-in';
export const POLL_PATH = '/api/auth/operator-sign-in/poll';
const MAX_LABEL_LENGTH = 64;
const UNREACHABLE_RETRIES = 5;

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
export async function login(ctx, options, { wait = sleep, now = () => Date.now(), agentFactory = startAgent, deviceKeyFactory = openDeviceKey } = {}) {
  const label = (options.label ?? defaultDeviceLabel()).trim();
  if (!label || label.length > MAX_LABEL_LENGTH || /\p{Cc}/u.test(label))
    throw usage('invalid-option', `--label must be 1 to ${MAX_LABEL_LENGTH} printable characters.`);
  const key = await agentFactory(ctx.configDir);
  let attached = false;
  try {
    // Retire the ES-3 disk key without reading it. Every sign-in gets a fresh memory key.
    fs.rmSync(path.join(ctx.configDir, 'operator-device-key.json'), { force: true });
    ctx.output.note('Created a session key in the resident studio-op agent memory.');

    const started = await ctx.client.request('POST', START_PATH, { body: {
      publicKey: key.publicJwk, deviceLabel: label, operatorAgent: options.agent ?? null, classes: options.class ?? [],
    } });
    const { provider, note } = deviceKeyFactory(ctx.configDir, { hardwareDeviceKeys: ctx.config.hardwareDeviceKeys });
    let certification;
    let bindingNote = note;
    try { certification = certifySession(provider, key.thumbprint, started.signInId); }
    catch {
      bindingNote = 'Device binding: memory. Hardware signing failed; this sign-in uses the resident session key.';
    }
    if (certification) {
      const { proof } = await agentCall(key.endpoint, { operation: 'poll', url: String(ctx.client.url(POLL_PATH)), pollHandle: started.pollHandle });
      // Register certification before displaying the code to the Owner.
      const certified = await ctx.client.send('POST', POLL_PATH, { body: { pollHandle: started.pollHandle, certification }, headers: { DPoP: proof } });
      if (certified.status !== 200) throw failure('POST', POLL_PATH, certified);
      if (certified.body?.bindingKind !== provider.bindingKind)
        bindingNote = 'Device binding: memory. Studio could not verify the device certification; this device is unknown.';
    }
    ctx.output.note(bindingNote);
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
      try {
        const { proof } = await agentCall(key.endpoint, { operation: 'poll', url: String(ctx.client.url(POLL_PATH)), pollHandle: started.pollHandle });
        answer = await ctx.client.send('POST', POLL_PATH, { body: { pollHandle: started.pollHandle }, headers: { DPoP: proof } });
      }
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
          await agentCall(key.endpoint, { operation: 'attach', token: body.token, expiresAt: body.expiresAt, baseUrl: ctx.client.base.href });
          await stopAgent(ctx.configDir);
          writePrivateFile(path.join(ctx.configDir, AGENT_FILE), JSON.stringify({ endpoint: key.endpoint, tokenHash: digest(body.token), thumbprint: key.thumbprint }));
          attached = true;
          const configUpdated = selectSessionCredential(ctx.configDir, ctx.config);
          const data = {
            operatorAgentId: body.operatorAgentId, sessionId: body.sessionId, actor: body.actor, delegatedBy: body.delegatedBy,
            expiresAt: body.expiresAt, classes: body.classes ?? [], keyThumbprint: body.keyThumbprint ?? key.thumbprint,
            bindingKind: body.bindingKind ?? 'memory', deviceLabel: label, file, mode: '600', configUpdated,
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
  } finally {
    if (!attached) await agentCall(key.endpoint, { operation: 'stop' }).catch(() => {});
  }
}
