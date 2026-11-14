// A Studio API client that signs in like a person: POST /api/auth/login sets
// the session cookie and returns the CSRF token, which every write carries.
// The product scenario holds one client per simulated person, so each request
// is judged against that person's role bindings.
import { createHmac } from 'node:crypto';

import { OperationError, failure } from '../../../tools/studio-op/src/studio.mjs';

const REQUEST_TIMEOUT_MS = 30_000;
const SESSION_COOKIE = '__Host-agentstudio-session';
const LOOPBACK_SESSION_COOKIE = 'agentstudio-session';
const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

export class SessionClient {
  constructor({ url, clientId = 'product-scenario', fetchImpl = fetch }) {
    let base;
    try { base = new URL(url); }
    catch { throw new OperationError('invalid-url', `The Studio URL "${url}" is not a URL.`); }
    if (base.protocol !== 'https:' && !isLoopback(base.hostname))
      throw new OperationError('session-requires-https', 'A password is sent only over HTTPS or to a loopback address.');
    this.base = base;
    this.clientId = clientId;
    this.fetch = fetchImpl;
    this.cookies = new Map();
    this.csrfToken = null;
    this.user = null;
  }

  get origin() { return this.base.origin; }

  url(path, query = {}) {
    const root = this.base.href.endsWith('/') ? this.base.href : `${this.base.href}/`;
    const target = new URL(path.replace(/^\/+/, ''), root);
    for (const [key, value] of Object.entries(query))
      if (value !== undefined && value !== null && value !== '') target.searchParams.set(key, String(value));
    return target;
  }

  /** Signs in; afterwards every request carries the session. Returns the user. */
  async signIn({ login, password, totpCode }) {
    const body = { username: login, password, ...(totpCode ? { secondFactorCode: totpCode } : {}) };
    const answer = await this.send('POST', '/api/auth/login', { body });
    if (answer.status < 200 || answer.status >= 300) throw failure('POST', '/api/auth/login', answer);
    this.csrfToken = answer.body?.csrfToken ?? null;
    this.user = answer.body?.user ?? null;
    return this.user;
  }

  async signOut() {
    if (!this.cookies.size) return;
    await this.send('POST', '/api/auth/logout', { body: {} });
    this.cookies.clear();
    this.csrfToken = null;
  }

  get signedIn() { return Boolean(this.csrfToken); }

  /** The session cookie, for handing the same session to the browser. */
  sessionCookie() {
    for (const name of [SESSION_COOKIE, LOOPBACK_SESSION_COOKIE])
      if (this.cookies.has(name)) return { name, value: this.cookies.get(name) };
    return null;
  }

  headers(method, extra = {}) {
    const cookie = [...this.cookies].map(([name, value]) => `${name}=${value}`).join('; ');
    return {
      Accept: 'application/json',
      'X-Client-Id': this.clientId,
      // Studio admits a session request only from its own pages (BrowserOriginPolicy).
      Origin: this.base.origin,
      'Sec-Fetch-Site': 'same-origin',
      ...(cookie ? { Cookie: cookie } : {}),
      ...(!SAFE_METHODS.has(method) && this.csrfToken ? { 'X-CSRF-Token': this.csrfToken } : {}),
      ...extra,
    };
  }

  /**
   * Sends one request and returns { status, body } without judging the status.
   * `body` is sent as JSON, `form` (FormData or encoded bytes) as is; `raw`
   * keeps a text answer such as a card file whole instead of parsing it.
   */
  async send(method, path, { query, body, form, raw = false, headers = {}, timeoutMs = REQUEST_TIMEOUT_MS } = {}) {
    let response;
    const json = body !== undefined && form === undefined;
    try {
      response = await this.fetch(this.url(path, query), {
        method,
        headers: this.headers(method, { ...(json ? { 'Content-Type': 'application/json' } : {}), ...headers }),
        body: form ?? (json ? JSON.stringify(body) : undefined),
        signal: AbortSignal.timeout(timeoutMs),
        redirect: 'manual',
      });
    } catch (error) {
      throw new OperationError('studio-unreachable',
        `Studio at ${this.base.origin} did not answer (${error.cause?.code ?? error.name}).`);
    }
    this.keepCookies(response.headers);
    const text = await response.text();
    if (raw) return { status: response.status, body: text };
    let parsed = null;
    if (text) {
      try { parsed = JSON.parse(text); } catch { parsed = { message: text.slice(0, 500) }; }
    }
    return { status: response.status, body: parsed };
  }

  /** Sends one request and throws for any non-2xx answer. */
  async request(method, path, options = {}) {
    const answer = await this.send(method, path, options);
    if (answer.status >= 200 && answer.status < 300) return answer.body;
    throw failure(method, path, answer);
  }

  get(path, query) { return this.request('GET', path, { query }); }
  post(path, body = {}) { return this.request('POST', path, { body }); }
  put(path, body = {}) { return this.request('PUT', path, { body }); }
  delete(path) { return this.request('DELETE', path); }

  keepCookies(headers) {
    const values = typeof headers.getSetCookie === 'function' ? headers.getSetCookie() : [];
    for (const line of values) {
      const [pair] = line.split(';');
      const index = pair.indexOf('=');
      if (index <= 0) continue;
      const name = pair.slice(0, index).trim();
      const value = pair.slice(index + 1).trim();
      if (/max-age=0|expires=thu, 01 jan 1970/i.test(line) || !value) this.cookies.delete(name);
      else this.cookies.set(name, value);
    }
  }
}

/** RFC 6238 time-based code (SHA-1, 30 s, 6 digits) from a base32 secret. */
export function totp(secret, nowMs) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const ch of secret.replace(/[\s=]/g, '').toUpperCase()) {
    const value = alphabet.indexOf(ch);
    if (value < 0) throw new OperationError('totp-secret-invalid', 'The authenticator secret is not base32.');
    bits += value.toString(2).padStart(5, '0');
  }
  const key = Buffer.from((bits.match(/.{8}/g) ?? []).map(byte => parseInt(byte, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(nowMs / 30_000)));
  const digest = createHmac('sha1', key).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  const code = (digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return String(code).padStart(6, '0');
}

function isLoopback(hostname) {
  return hostname === 'localhost' || hostname === '::1' || hostname === '[::1]' || /^127\./.test(hostname);
}
