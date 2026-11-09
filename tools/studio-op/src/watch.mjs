import fs from 'node:fs';
import path from 'node:path';
import { EXIT, eventExitCode } from './exit-codes.mjs';
import { SseParser } from './sse.mjs';
import { OperationError, failure } from './studio.mjs';

export const LOCK_FILE = 'studio-op-watch.lock';
export const CURSOR_FILE = 'studio-op-watch.cursor.json';
const RECONNECT_MAX_MS = 15_000;

/**
 * Guardrail "One watcher at a time". The local lock keeps a second watcher on
 * this machine from even asking; the server lease keeps a watcher on any
 * other machine out. The lock file sits next to the token file and records
 * the process and lease that hold it.
 */
export function acquireLocalLock(directory, details) {
  fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
  const file = path.join(directory, LOCK_FILE);
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const handle = fs.openSync(file, 'wx', 0o600);
      fs.writeSync(handle, JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString(), ...details }));
      fs.closeSync(handle);
      return {
        file,
        update(extra) {
          fs.writeFileSync(file, JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString(), ...details, ...extra }),
            { mode: 0o600 });
        },
        release() { try { fs.unlinkSync(file); } catch (error) { if (error.code !== 'ENOENT') throw error; } },
      };
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      const holder = readJson(file);
      if (holder?.pid && holder.pid !== process.pid && isAlive(holder.pid))
        throw new OperationError('watcher-active',
          `Another studio-op watch (process ${holder.pid}, since ${holder.startedAt ?? 'unknown'}) holds ${file}.`,
          { exitCode: EXIT.WATCHER_ACTIVE, body: { scope: 'local-lock', pid: holder.pid, startedAt: holder.startedAt ?? null,
            leaseId: holder.leaseId ?? null } });
      // The holder died without cleaning up: the lock is stale.
      fs.rmSync(file, { force: true });
    }
  }
  throw new OperationError('watcher-lock-unavailable', `Cannot create ${file}.`, { exitCode: EXIT.FAILURE });
}

function isAlive(pid) {
  try { process.kill(pid, 0); return true; }
  catch (error) { return error.code === 'EPERM'; }
}

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

/** The cursor the last watch stopped at, kept per Studio origin. */
export function savedCursor(directory, origin) {
  const saved = readJson(path.join(directory, CURSOR_FILE));
  return saved?.origin === origin && /^\d+$/.test(String(saved.cursor ?? '')) ? String(saved.cursor) : null;
}

function saveCursor(directory, origin, cursor) {
  if (cursor === null || cursor === undefined) return;
  fs.writeFileSync(path.join(directory, CURSOR_FILE), JSON.stringify({ origin, cursor: String(cursor) }), { mode: 0o600 });
}

/**
 * Takes the watcher lease, blocks on the decision stream, and resolves with
 * the first actionable event and its class exit code. Heartbeats keep the
 * lease; a lost lease ends the watch with {@link EXIT.WATCHER_LEASE_LOST}.
 * The lease and the lock are released on every exit path.
 */
export async function watchDecisions({ client, output, directory, session, takeOver, reason, after, fromStart,
  timeoutSeconds }) {
  const origin = client.base.origin;
  const lock = acquireLocalLock(directory, { origin, session: session ?? null });
  let lease = null;
  let heartbeat = null;
  let timer = null;
  const streamAbort = new AbortController();
  const signals = [];
  try {
    const granted = await client.send('POST', '/api/operator/watcher/lease',
      { body: { session: session ?? null, takeOver: Boolean(takeOver), reason: reason ?? null } });
    if (granted.status === 409) {
      const body = granted.body ?? {};
      throw new OperationError('watcher-active',
        `${body.holder ?? 'Another watcher'}${body.session ? ` (session ${body.session})` : ''} holds the watcher lease; last heartbeat ${body.lastHeartbeatUtc ?? 'unknown'}.`,
        { exitCode: EXIT.WATCHER_ACTIVE, body: { scope: 'server-lease', holder: body.holder ?? null,
          session: body.session ?? null, lastHeartbeatUtc: body.lastHeartbeatUtc ?? null } });
    }
    if (granted.status < 200 || granted.status >= 300) throw failure('POST', '/api/operator/watcher/lease', granted);
    lease = granted.body.lease;
    lock.update({ leaseId: lease.leaseId });
    if (granted.body.outcome === 'taken-over')
      output.note(`Took over the watcher lease from ${granted.body.previous?.holder ?? 'the previous holder'}; the take-over is audited.`);

    let cursor = after ?? (fromStart ? '0' : savedCursor(directory, origin));
    let done = false;
    const finished = new Promise(resolve => {
      const finish = (exitCode, event, why) => {
        if (done) return;
        done = true;
        resolve({ exitCode, event, reason: why, cursor });
      };
      const beatMs = Math.max(1, Number(lease.heartbeatSeconds) || 30) * 1000;
      heartbeat = setInterval(async () => {
        try {
          const answer = await client.send('POST', `/api/operator/watcher/lease/${encodeURIComponent(lease.leaseId)}/heartbeat`);
          if (answer.status === 409) { lease = null; finish(EXIT.WATCHER_LEASE_LOST, null, 'watcher-lease-lost'); }
        } catch (error) {
          output.note(`Heartbeat failed (${error.code ?? error.message}); retrying.`);
        }
      }, beatMs);
      if (timeoutSeconds) timer = setTimeout(() => finish(EXIT.OK, null, 'timeout'), timeoutSeconds * 1000);
      for (const name of ['SIGINT', 'SIGTERM']) {
        const handler = () => finish(EXIT.INTERRUPTED, null, 'interrupted');
        process.once(name, handler);
        signals.push([name, handler]);
      }

      const onFrame = frame => {
        // Frames after the reported event stay unread, so the next watch gets them.
        if (done) return;
        if (frame.id) cursor = frame.id;
        let event;
        try { event = JSON.parse(frame.data); } catch { event = { type: frame.event, data: frame.data }; }
        event.type ??= frame.event;
        const code = eventExitCode(frame.event);
        if (code === null) { saveCursor(directory, origin, cursor); return; }
        if (frame.event === 'stream.gap') {
          // Resume from the oldest kept event next time; the gap itself is the wake-up.
          const oldest = Number(event.oldestCursor);
          cursor = Number.isInteger(oldest) && oldest > 0 ? String(oldest - 1) : event.headCursor ?? cursor;
        }
        saveCursor(directory, origin, cursor);
        finish(code, event, 'event');
      };

      (async () => {
        let delay = 1000;
        let announced = false;
        while (!streamAbort.signal.aborted) {
          try {
            const response = await client.stream('/api/operator/decisions/stream',
              { query: { after: cursor ?? undefined }, signal: streamAbort.signal });
            if (!announced) {
              output.note(`Watching the decision stream at ${origin} with lease ${lease.leaseId}${cursor ? ` after cursor ${cursor}` : ' from now'}.`);
              announced = true;
            }
            delay = 1000;
            const parser = new SseParser(onFrame);
            const decoder = new TextDecoder();
            for await (const chunk of response.body) parser.push(decoder.decode(chunk, { stream: true }));
          } catch (error) {
            if (streamAbort.signal.aborted) return;
            if (error.exitCode === EXIT.CREDENTIAL || error.exitCode === EXIT.FORBIDDEN) {
              finish(error.exitCode, null, error.code);
              return;
            }
            output.note(`Decision stream interrupted (${error.code ?? error.message}); reconnecting.`);
          }
          if (streamAbort.signal.aborted) return;
          await new Promise(resolve => setTimeout(resolve, delay));
          delay = Math.min(delay * 2, RECONNECT_MAX_MS);
        }
      })();
    });
    return await finished;
  } finally {
    clearInterval(heartbeat);
    clearTimeout(timer);
    for (const [name, handler] of signals) process.removeListener(name, handler);
    streamAbort.abort();
    if (lease) {
      try { await client.send('DELETE', `/api/operator/watcher/lease/${encodeURIComponent(lease.leaseId)}`); }
      catch (error) { output.note(`Could not release the watcher lease (${error.code ?? error.message}); it expires on its own.`); }
    }
    lock.release();
  }
}
